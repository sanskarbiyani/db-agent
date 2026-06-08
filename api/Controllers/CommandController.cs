using Microsoft.AspNetCore.Mvc;
using DbAgent.Api.Kafka;
using DbAgent.Common;
using DbAgent.Common.Messages;
using DbAgent.Api.Common;

namespace DbAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CommandController : ControllerBase
{
    private readonly KafkaProducer _kafkaProducer;
    private readonly ILogger<CommandController> _logger;
    private readonly IDatabaseService _databaseService;

    public CommandController(KafkaProducer kafkaProducer, ILogger<CommandController> logger, IDatabaseService databaseService)
    {
        _kafkaProducer = kafkaProducer;
        _logger = logger;
        _databaseService = databaseService;
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

        bool result = await _databaseService.InsertIntoQueryAttempt(message.OriginalCommand, message.GeneratedSql, message.ExecutionId);

        if (!result)
        {
            _logger.LogError(
                "Failed to log command in database. ExecutionId: {ExecutionId}",
                message.ExecutionId);

            return StatusCode(500, new CommandFailureResponse("Service temporarily unavailable. Please try again later.", "DB_UNAVAILABLE"));
        }

        var kafkaResult = await _kafkaProducer.ProduceAsync(KafkaTopics.PendingQueries, message);

        if (!kafkaResult)
        {
            _logger.LogError(
                "Failed to enqueue command in Kafka. ExecutionId: {ExecutionId}",
                message.ExecutionId);
            return StatusCode(500, new CommandFailureResponse("Service temporarily unavailable. Please try again later.", "KAFKA_UNAVAILABLE"));
        }

        _logger.LogInformation(
            "Command received and queued. ExecutionId: {ExecutionId}",
            message.ExecutionId);

        return Accepted(new CommandSuccessResponse(message.ExecutionId, "Queued"));
    }
}

public record CommandRequest(string Command);

public record CommandSuccessResponse(Guid ExecutionId, string Status);
public record CommandFailureResponse(string ErrorMessage, string Code);