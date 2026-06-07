namespace DbAgent.Common;

public static class KafkaTopics
{
    public const string PendingQueries = "pending-queries";
    public const string RetryQueue = "retry-queue";
    public const string FixableSchemaErrors = "fixable-schema-errors";
    public const string FailedQueries = "failed-queries";
}