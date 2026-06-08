namespace DbAgent.DbExecutor.Services;

public enum ErrorCategory
{
    ImmediateRetry,     // transient errors that should be retried immediately
    BackoffRetry,       // transient errors that should be retried with backoff (e.g. timeouts, locks)
    AgentFixable,       // errors that the agent can fix by modifying the SQL (e.g. schema issues)
    AlreadySatisfied,   // errors that indicate the command was already satisfied (e.g. table already exists)
    ManualReview,       // errors that require manual review but might be fixable (e.g. permission denied)
    Fatal,              // errors that indicate a fatal issue that should not be retried (e.g. syntax error)
    NonFixable          // errors that are not fixable and should be marked as failed
}

public static class ErrorClassifier
{
    public static ErrorCategory Classify(string errorType, string errorMessage)
    {
        // PostgreSQL error codes
        // Full list: https://www.postgresql.org/docs/current/errcodes-appendix.html
        return errorType switch
        {
            // Connection errors — immediate retry
            "CONNECTION_ERROR" => ErrorCategory.ImmediateRetry,
            "08000" => ErrorCategory.ImmediateRetry, // connection exception
            "08001" => ErrorCategory.ImmediateRetry, // SQL client unable to establish connection
            "08003" => ErrorCategory.ImmediateRetry, // connection does not exist
            "08004" => ErrorCategory.ImmediateRetry, // SQL server rejected connection
            "08006" => ErrorCategory.ImmediateRetry, // connection failure
            "57P01" => ErrorCategory.ImmediateRetry, // admin shutdown

            // Timeout and lock errors — backoff retry
            "57014" => ErrorCategory.BackoffRetry,   // query cancelled (timeout)
            "55P03" => ErrorCategory.BackoffRetry,   // lock not available
            "40001" => ErrorCategory.BackoffRetry,   // serialization failure (deadlock)
            "40P01" => ErrorCategory.BackoffRetry,   // deadlock detected
            "53300" => ErrorCategory.BackoffRetry,   // too many connections
            "53100" => ErrorCategory.BackoffRetry,   // disk full
            "53200" => ErrorCategory.BackoffRetry,   // out of memory
            "53400" => ErrorCategory.BackoffRetry,   // configuration limit exceeded (Check)

            // Schema errors — agent fixable
            "42601" => ErrorCategory.AgentFixable,     // syntax error

            // Already satisfied  - Mark as not required
            "42P07" => ErrorCategory.AlreadySatisfied,  // table already exists
            "42701" => ErrorCategory.AlreadySatisfied,  // column already exists

            // Manual review — might be fixable but requires human intervention
            "42501" => ErrorCategory.ManualReview,     // insufficient privilege
            "42883" => ErrorCategory.ManualReview,     // undefined function
            "42804" => ErrorCategory.ManualReview,     // datatype mismatch (could be fixable but needs review)

            // Non fixable errors — straight to failed
            "42P01" => ErrorCategory.Fatal,     // table not found
            "42703" => ErrorCategory.Fatal,     // column not found
            "23000" => ErrorCategory.Fatal,     // integrity constraint violation
            "23505" => ErrorCategory.Fatal,     // unique violation
            "UNKNOWN_ERROR" => ErrorCategory.Fatal,

            // Default — non fixable if unknown
            _ => ErrorCategory.Fatal
        };
    }
}