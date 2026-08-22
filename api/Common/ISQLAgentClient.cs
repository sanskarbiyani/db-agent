using DbAgent.Common.Models;

namespace DbAgent.Api.Common
{
    public interface ISQLAgentClient
    {
        public Task<PythonAgentResponse?> GetGeneratedSql(string command, SchemaContext schema, string executionId);
    }
}
