using DbAgent.Common.Messages;
using DbAgent.DbExecutor.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace DbAgent.DbExecutor.Interfaces
{
    public interface IDatabaseService
    {
        Task<(ExecutionResult, ErrorCategory? errorCategory)> ExecuteSqlAsync(QueryMessage message);
        Task LogAttemptAsync(Guid executionId, int attemptNumber, string errorType, string errorMessage, bool resolved);
        Task UpdateFailedStatus(Guid executionId);
    }
}
