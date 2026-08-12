using DbAgent.Api.Models;

namespace DbAgent.Api.Common
{
    public interface ISQLAgentClient
    {
        public Task<GenerateSqlResponse?> GetGeneratedSql(string command, SchemaContext schema, string executionId);
    }
}
