namespace DbAgent.Api.Common;

public interface IDatabaseService
{
    public Task<bool> InsertIntoQueryAttempt(string originalCmd, string generatedSql, Guid id);
}
