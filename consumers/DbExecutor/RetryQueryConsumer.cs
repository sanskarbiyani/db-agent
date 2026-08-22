using Confluent.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;
using DbAgent.DbExecutor.Services;
using System.Text.Json;
using static Confluent.Kafka.ConfigPropertyNames;

namespace DbAgent.DbExecutor
{
    public class RetryQueryConsumer : BackgroundService
    {
        private readonly ILogger<RetryQueryConsumer> _logger;
        private readonly IConfiguration _configuration;
        private readonly RetryChannel _retryChannel;

        public RetryQueryConsumer(
            ILogger<RetryQueryConsumer> logger,
            IConfiguration configuration,
            RetryChannel retryChannel)
        {
            _logger = logger;
            _configuration = configuration;
            _retryChannel = retryChannel;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            var config = new ConsumerConfig
            {
                BootstrapServers = _configuration["Kafka:BootstrapServers"],
                GroupId = _configuration["Kafka:RetryConsumerGroup"],
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            };
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(KafkaTopics.RetryQueue);
            _logger.LogInformation("RetryQueryConsumer started, listening on {Topic}", KafkaTopics.RetryQueue);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(stoppingToken);
                    var message = JsonSerializer.Deserialize<RetryMessage>(result.Message.Value);
                    if (message is null)
                    {
                        _logger.LogWarning("Received null retry message, skipping");
                        consumer.Commit(result);
                        continue;
                    }

                    _logger.LogInformation(
                        "Retrying command, pushed to channel. ExecutionId: {ExecutionId}, Command: {Command}",
                        message.ExecutionId, message.OriginalCommand);

                    await _retryChannel.RetryQueryChannel.Writer.WriteAsync(message, stoppingToken);

                    consumer.Commit(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing retry query");
                }
            }
        }
    }
}
