using Confluent.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;
using DbAgent.DbExecutor.Interfaces;
using DbAgent.DbExecutor.Services;
using System.Text.Json;

namespace DbAgent.DbExecutor;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IDatabaseService _databaseService;

    public Worker(
        ILogger<Worker> logger,
        IConfiguration configuration,
        IDatabaseService databaseService)
    {
        _logger = logger;
        _configuration = configuration;
        _databaseService = databaseService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _configuration["Kafka:BootstrapServers"],
            GroupId = _configuration["Kafka:ConsumerGroup"],
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(KafkaTopics.PendingQueries);

        _logger.LogInformation("DbExecutor started, listening on {Topic}", KafkaTopics.PendingQueries);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                var message = JsonSerializer.Deserialize<QueryMessage>(result.Message.Value);

                if (message is null)
                {
                    _logger.LogWarning("Received null message, skipping");
                    consumer.Commit(result);
                    continue;
                }

                _logger.LogInformation(
                    "Processing command. ExecutionId: {ExecutionId}, Command: {Command}",
                    message.ExecutionId, message.OriginalCommand);

                var (executionResult, category) = await _databaseService.ExecuteSqlAsync(message);

                if (executionResult.IsSuccess)
                {
                    _logger.LogInformation(
                        "Execution successful. ExecutionId: {ExecutionId}",
                        message.ExecutionId);
                }
                else
                {
                    _logger.LogWarning(
                        "Execution failed. ExecutionId: {ExecutionId}, ErrorType: {ErrorType}, Category: {Category}",
                        message.ExecutionId, executionResult.ErrorType, category);

                    if(category == null)
                    {
                        _logger.LogError(
                            "Error category is null for ExecutionId: {ExecutionId}. Defaulting to NonFixable.",
                            message.ExecutionId);
                        category = ErrorCategory.NonFixable;
                    }

                    await RouteErrorAsync(message, executionResult, (ErrorCategory)category);
                }

                consumer.Commit(result);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in consumer loop");
            }
        }

        consumer.Close();
    }

    private async Task RouteErrorAsync(
        QueryMessage message,
        ExecutionResult result,
        ErrorCategory category)
    {
        // Kafka producer for routing errors
        var producerConfig = new Confluent.Kafka.ProducerConfig
        {
            BootstrapServers = _configuration["Kafka:BootstrapServers"]
        };

        using var producer = new Confluent.Kafka.ProducerBuilder<string, string>(producerConfig).Build();

        var retryMessage = new RetryMessage
        {
            ExecutionId = message.ExecutionId,
            OriginalCommand = message.OriginalCommand,
            GeneratedSql = message.GeneratedSql,
            ErrorType = result.ErrorType!,
            ErrorMessage = result.ErrorMessage!,
            AttemptNumber = 1,
            CreatedAt = DateTime.UtcNow
        };

        var topic = category switch
        {
            ErrorCategory.ImmediateRetry => KafkaTopics.RetryQueue,
            ErrorCategory.BackoffRetry => KafkaTopics.RetryQueue,
            ErrorCategory.AgentFixable => KafkaTopics.FixableSchemaErrors,
            ErrorCategory.NonFixable => KafkaTopics.FailedQueries,
            _ => KafkaTopics.FailedQueries
        };

        var json = JsonSerializer.Serialize(retryMessage);
        await producer.ProduceAsync(topic, new Confluent.Kafka.Message<string, string>
        {
            Key = message.ExecutionId.ToString(),
            Value = json
        });

        _logger.LogInformation(
            "Routed ExecutionId: {ExecutionId} to topic: {Topic}",
            message.ExecutionId, topic);

        // Not Required because we are updating the status in the query execution table to retring.
        // Which indicated that first attempt has failed.
        // This insert just consumes extra entry in the query attempt table.
        //await _databaseService.LogAttemptAsync(
        //    message.ExecutionId,
        //    1,
        //    result.ErrorType!,
        //    result.ErrorMessage!,
        //    false);
    }
}