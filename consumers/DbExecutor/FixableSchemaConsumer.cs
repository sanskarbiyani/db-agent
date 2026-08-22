using Confluent.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;
using DbAgent.Common.Models;
using DbAgent.DbExecutor.Interfaces;
using DbAgent.DbExecutor.logs;
using DbAgent.DbExecutor.Models;
using DbAgent.DbExecutor.Services;
using System.Text.Json;

namespace DbAgent.DbExecutor
{
    public class FixableSchemaConsumer : BackgroundService
    {
        private readonly ILogger<FixableSchemaConsumer> _logger;
        private readonly IDatabaseService _databaseService;
        private readonly IConfiguration _configuration;
        private readonly IFixAgentClient _fixAgentClient;

        public FixableSchemaConsumer(
            ILogger<FixableSchemaConsumer> logger,
            IConfiguration configuration,
            IDatabaseService databaseService,
            IFixAgentClient fixAgentClient)
        {
            _logger = logger;
            _databaseService = databaseService;
            _configuration = configuration;
            _fixAgentClient = fixAgentClient;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Defining the consumer
            var config = new ConsumerConfig
            {
                BootstrapServers = _configuration["Kafka:BootstrapServers"],
                GroupId = _configuration["Kafka:ConsumerGroup"],
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            };
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(KafkaTopics.FixableSchemaErrors);
            _logger.LogInformation("FixableSchemaConsumer started, listening on {Topic}", KafkaTopics.FixableSchemaErrors);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(stoppingToken);
                    var message = JsonSerializer.Deserialize<RetryMessage>(result.Message.Value);
                    if (message is null)
                    {
                        _logger.LogWarning("Received null message from {Topic}", KafkaTopics.FixableSchemaErrors);
                        continue;
                    }

                    _logger.LogInformation("Received message from {Topic}: {Message}", KafkaTopics.FixableSchemaErrors, result.Message.Value);

                    _ = ProcessFixAsync(message, stoppingToken);

                    consumer.Commit();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error consuming message from {Topic}", KafkaTopics.FixableSchemaErrors);
                }
            }
        }

        private async Task ProcessFixAsync(RetryMessage message, CancellationToken ct)
        {
            var request = new FixAgentRequest(message.OriginalCommand, JsonSerializer.Deserialize<SchemaContext>(message.Schema)!, message.GeneratedSql, message.ErrorMessage);

            var resp = await _fixAgentClient.GetFixSql(request, message.ExecutionId.ToString());

            if (resp is null)
            {
                await _databaseService.LogAttemptAsync(message.ExecutionId, 0, "FIX_AGENT_ERROR", "Failed to get fix SQL", false, message.GeneratedSql, "fix");
            }
            else
            {
                if (string.IsNullOrEmpty(resp.Error) && !string.IsNullOrEmpty(resp.Sql))
                {
                    message.GeneratedSql = resp.Sql;
                    var (executionResult, errCategory) = await _databaseService.ExecuteSqlAsync(message);
                    await _databaseService.LogAttemptAsync(message.ExecutionId, 0, message.ErrorType ?? "", message.ErrorMessage ?? "", executionResult.IsSuccess, message.GeneratedSql, "fix");

                    if (!executionResult.IsSuccess)
                    {
                        if (errCategory == ErrorCategory.ImmediateRetry || errCategory == ErrorCategory.BackoffRetry)
                        {
                            var retryMessage = new RetryMessage
                            {
                                ExecutionId = message.ExecutionId,
                                OriginalCommand = message.OriginalCommand,
                                GeneratedSql = message.GeneratedSql,
                                ErrorType = executionResult.ErrorType!,
                                ErrorMessage = executionResult.ErrorMessage!,
                                AttemptNumber = 1,
                                CreatedAt = DateTime.UtcNow,
                                Schema = message.Schema
                            };

                            var topic = KafkaTopics.RetryQueue;

                            var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _configuration["Kafka:BootstrapServers"] }).Build();

                            var json = JsonSerializer.Serialize(retryMessage);
                            await producer.ProduceAsync(topic, new Confluent.Kafka.Message<string, string>
                            {
                                Key = message.ExecutionId.ToString(),
                                Value = json
                            });

                            _logger.LogInformation(
                                "Routed ExecutionId: {ExecutionId} to topic: {Topic}",
                                message.ExecutionId, topic);

                        }
                        else
                        {
                            await _databaseService.UpdateFailedStatus(message.ExecutionId);
                        }
                    }
                }
                else
                {
                    await _databaseService.LogAttemptAsync(message.ExecutionId, 0, "FIX_AGENT_ERROR", resp.Error, false, "", "fix");
                    await _databaseService.UpdateFailedStatus(message.ExecutionId);
                }
            }
        }
    }
}
