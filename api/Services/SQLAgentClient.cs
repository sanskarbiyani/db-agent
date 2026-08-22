using DbAgent.Api.Common;
using DbAgent.Common.Models;

namespace DbAgent.Api.Services
{
    public class SQLAgentClient: ISQLAgentClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<SQLAgentClient> _logger;

        public SQLAgentClient(HttpClient httpClient, ILogger<SQLAgentClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<PythonAgentResponse?> GetGeneratedSql(string command, SchemaContext schema_context, string executionId)
        {
            try
            {
                var payload = new { command, schema_context };

                //return new PythonAgentResponse(
                //    "UPDATE fruits SET stock = 50 WHERE name = 'orange'",
                //    "",
                //    ""
                //);

                var resp = await _httpClient.PostAsJsonAsync("generate", payload);

                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadFromJsonAsync<PythonAgentResponse>();
                    _logger.LogError("Error generating SQL: {Error}; executionId: {ExecutionId}", errorBody?.Error, executionId);
                    return errorBody ?? new PythonAgentResponse("", "Unknown error from agent service", null);
                }
                return await resp.Content.ReadFromJsonAsync<PythonAgentResponse>();
            }
            catch (TaskCanceledException tcex)
            {
                _logger.LogError("Request to generate SQL was canceled: {Error}; executionId: {ExecutionId}", tcex.Message, executionId);
                return new PythonAgentResponse("", "Request was canceled", null);
            }
            catch (HttpRequestException httpex)
            {
                _logger.LogError("Error occurred while requesting SQL generation: {Error}; executionId: {ExecutionId}", httpex.Message, executionId);
                return new PythonAgentResponse("", "Error occurred while requesting SQL generation", null);
            }
            catch (Exception ex)
            {
                _logger.LogError("An unexpected error occurred: {Error}; executionId: {ExecutionId}", ex.Message, executionId);
                return new PythonAgentResponse("", "An unexpected error occurred", null);
            }
        }
    }
}
