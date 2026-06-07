using Microsoft.AspNetCore.Mvc;
using DbAgent.Api.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;

namespace DbAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CommandController : ControllerBase
{
    private readonly KafkaProducer _kafkaProducer;
    private readonly ILogger<CommandController> _logger;

    public CommandController(KafkaProducer kafkaProducer, ILogger<CommandController> logger)
    {
        _kafkaProducer = kafkaProducer;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> ExecuteCommand([FromBody] CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            return BadRequest("Command cannot be empty");

        var message = new QueryMessage
        {
            ExecutionId = Guid.NewGuid(),
            OriginalCommand = request.Command,
            GeneratedSql = request.Command, // Hardcoded for now — agent replaces this later
            CreatedAt = DateTime.UtcNow
        };

        await _kafkaProducer.ProduceAsync(KafkaTopics.PendingQueries, message);

        _logger.LogInformation(
            "Command received and queued. ExecutionId: {ExecutionId}",
            message.ExecutionId);

        return Accepted(new { message.ExecutionId, Status = "Queued" });
    }
}

public record CommandRequest(string Command);