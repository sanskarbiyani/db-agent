using DbAgent.Api.Common;
using DbAgent.Api.Kafka;
using DbAgent.Api.Models;
using DbAgent.Common;
using DbAgent.Common.Messages;
using Microsoft.AspNetCore.Mvc;

namespace DbAgent.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CommandController : ControllerBase
{
    private readonly KafkaProducer _kafkaProducer;
    private readonly ILogger<CommandController> _logger;
    private readonly IDatabaseService _databaseService;
    private readonly ISQLAgentClient _sqlAgentClient;

    public CommandController(KafkaProducer kafkaProducer, ILogger<CommandController> logger, IDatabaseService databaseService, ISQLAgentClient sqlAgentClient)
    {
        _kafkaProducer = kafkaProducer;
        _logger = logger;
        _databaseService = databaseService;
        _sqlAgentClient = sqlAgentClient;
    }

    [HttpPost]
    public async Task<IActionResult> ExecuteCommand([FromBody] CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
            return BadRequest("Command cannot be empty");

        var executionId = Guid.NewGuid();
        var schemaContext = new SchemaContext(
            Tables: new List<TableInfo>
            {
                new TableInfo(
                    Name: "fruits",
                    Columns: new List<ColumnInfo>
                    {
                        new ColumnInfo(Name: "name", DataType: "character varying", IsNullable: true),
                        new ColumnInfo(Name: "price", DataType: "numeric", IsNullable: false)
                    }
                ),
                new TableInfo(
                    Name: "orders",
                    Columns: new List<ColumnInfo>
                    {
                        new ColumnInfo(Name: "id", DataType: "integer", IsNullable: false),
                        new ColumnInfo(Name: "fruit_name", DataType: "character varying", IsNullable: false),
                        new ColumnInfo(Name: "quantity", DataType: "integer", IsNullable: true)
                    }
                )
            },
            Constraints: new List<ConstraintInfo>
            {
                new ConstraintInfo(TableName: "fruits", ColumnName: "name", ConstraintType: "UNIQUE")
            }
        );

        var sqlResult = await _sqlAgentClient.GetGeneratedSql(request.Command, schemaContext, executionId.ToString());

        if (sqlResult != null)
        {
            if (!string.IsNullOrWhiteSpace(sqlResult.Error))
            {
                return UnprocessableEntity(new CommandFailureResponse($"Agent rejected command: {sqlResult.Error}", "AGENT_REJECTED"));
            }
            if (string.IsNullOrWhiteSpace(sqlResult.Sql))
            {
                return StatusCode(502, new CommandFailureResponse("Agent returned no usable SQL.", "AGENT_NO_SQL"));
            }
            var message = new QueryMessage
            {
                ExecutionId = executionId,
                OriginalCommand = request.Command,
                GeneratedSql = sqlResult.Sql,
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
        else
        {
            _logger.LogError("SQL agent returned null. ExecutionId: {ExecutionId}", executionId);
            return StatusCode(502, new CommandFailureResponse("SQL agent unavailable.", "AGENT_UNAVAILABLE"));
        }
    }
}

public record CommandRequest(string Command);

public record CommandSuccessResponse(Guid ExecutionId, string Status);
public record CommandFailureResponse(string ErrorMessage, string Code);