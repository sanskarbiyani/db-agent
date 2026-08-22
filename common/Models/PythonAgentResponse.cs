namespace DbAgent.Common.Models
{
    public record PythonAgentResponse(string Sql, string Error, SchemaContext? Schema);
}
