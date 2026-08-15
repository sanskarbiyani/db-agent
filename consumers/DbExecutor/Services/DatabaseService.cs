using Dapper;
using DbAgent.Common.Messages;
using DbAgent.DbExecutor.Interfaces;
using Npgsql;

namespace DbAgent.DbExecutor.Services;

public class DatabaseService: IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("ErrorConnection")!;
        _logger = logger;
    }

    public async Task<(ExecutionResult, ErrorCategory? errorCategory)> ExecuteSqlAsync(QueryMessage message)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to database. ExecutionId: {ExecutionId}", message.ExecutionId);
            return (ExecutionResult.Failure("CONNECTION_ERROR", ex.Message), ErrorCategory.ImmediateRetry);
        }

        ExecutionResult executionResult;
        try
        {
            await connection.ExecuteAsync(message.GeneratedSql);

            _logger.LogInformation(
                "SQL executed successfully. ExecutionId: {ExecutionId}",
                message.ExecutionId);

            await UpdateExecutionStatusAsync(connection, message.ExecutionId, "success");
            executionResult = ExecutionResult.Success();
        }
        catch (PostgresException ex)
        {
            _logger.LogError(ex,
                "PostgreSQL error executing SQL. ExecutionId: {ExecutionId}, SqlState: {SqlState}",
                message.ExecutionId, ex.SqlState);

            executionResult = ExecutionResult.Failure(ex.SqlState!, ex.Message);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex,
                "Connection error executing SQL. ExecutionId: {ExecutionId}",
                message.ExecutionId);

            executionResult = ExecutionResult.Failure("CONNECTION_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error executing SQL. ExecutionId: {ExecutionId}",
                message.ExecutionId);
            executionResult = ExecutionResult.Failure("UNKNOWN_ERROR", ex.Message);
        }

        if (!executionResult.IsSuccess)
        {
            var errorCategory = ErrorClassifier.Classify(executionResult.ErrorType!, executionResult.ErrorMessage!);

            string errorStatus = "failed";
            errorStatus = errorCategory switch
            {
                ErrorCategory.ImmediateRetry or ErrorCategory.BackoffRetry => "retrying",
                ErrorCategory.AgentFixable => "fixing",
                ErrorCategory.AlreadySatisfied => "already_satisfied",
                ErrorCategory.ManualReview => "manual_review",
                ErrorCategory.Fatal or ErrorCategory.NonFixable => "failed",
                _ => errorStatus
            };

            await UpdateExecutionStatusAsync(connection, message.ExecutionId, errorStatus);
            return (executionResult, errorCategory);
        }
        else
            return (executionResult, null);
    }

    public async Task UpdateFailedStatus(Guid executionId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await UpdateExecutionStatusAsync(connection, executionId, "failed");
    }

    public async Task LogAttemptAsync(
        Guid executionId,
        int attemptNumber,
        string errorType,
        string errorMessage,
        bool resolved)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await connection.ExecuteAsync(@"
            INSERT INTO query_attempts
                (id, execution_id, attempt_number, error_type, error_message, attempted_at, resolved)
            VALUES
                (@Id, @ExecutionId, @AttemptNumber, @ErrorType, @ErrorMessage, @AttemptedAt, @Resolved)",
                new
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = executionId,
                    AttemptNumber = attemptNumber,
                    ErrorType = errorType,
                    ErrorMessage = errorMessage,
                    AttemptedAt = DateTime.UtcNow,
                    Resolved = resolved
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log attempt for ExecutionId: {ExecutionId}", executionId);
        }
    }

    private async Task UpdateExecutionStatusAsync(
    NpgsqlConnection connection,
    Guid executionId,
    string status)
    {
        await connection.ExecuteAsync(@"
        UPDATE query_executions 
        SET status = @Status 
        WHERE id = @Id",
            new { Id = executionId, Status = status });
    }
}

public class ExecutionResult
{
    public bool IsSuccess { get; private set; }
    public string? ErrorType { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static ExecutionResult Success() => new() { IsSuccess = true };

    public static ExecutionResult Failure(string errorType, string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorType = errorType,
        ErrorMessage = errorMessage
    };
}