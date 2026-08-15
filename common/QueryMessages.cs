namespace DbAgent.Common.Messages;

public class QueryMessage
{
    public Guid ExecutionId { get; set; }
    public string OriginalCommand { get; set; } = string.Empty;
    public string GeneratedSql { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class RetryMessage: QueryMessage
{
    public string ErrorType { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int AttemptNumber { get; set; }
}

public class FailedQueryMessage: QueryMessage
{
    public string ErrorType { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int TotalAttempts { get; set; }
    public DateTime FailedAt { get; set; }
}