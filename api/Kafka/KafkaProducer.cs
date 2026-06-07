using Confluent.Kafka;
using DbAgent.Common.Messages;
using System.Text.Json;

namespace DbAgent.Api.Kafka;

public class KafkaProducer : IDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<KafkaProducer> _logger;

    public KafkaProducer(IConfiguration configuration, ILogger<KafkaProducer> logger)
    {
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"],
            Acks = Acks.All,
            MessageTimeoutMs = 5000
        };

        _producer = new ProducerBuilder<string, string>(config).Build();
    }

    public async Task ProduceAsync<T>(string topic, T message)
    {
        var json = JsonSerializer.Serialize(message);
        var kafkaMessage = new Message<string, string>
        {
            Key = Guid.NewGuid().ToString(),
            Value = json
        };

        var result = await _producer.ProduceAsync(topic, kafkaMessage);
        _logger.LogInformation(
            "Message delivered to topic {Topic}, partition {Partition}, offset {Offset}",
            result.Topic, result.Partition, result.Offset);
    }

    public void Dispose()
    {
        _producer?.Flush(TimeSpan.FromSeconds(5));
        _producer?.Dispose();
    }
}