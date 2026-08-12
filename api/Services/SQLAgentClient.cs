using DbAgent.Api.Common;
using DbAgent.Api.Models;

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

        public async Task<GenerateSqlResponse?> GetGeneratedSql(string command, SchemaContext schema_context, string executionId)
        {
            try
            {
                var payload = new { command, schema_context };

                var resp = await _httpClient.PostAsJsonAsync("generate", payload);

                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadFromJsonAsync<GenerateSqlResponse>();
                    _logger.LogError("Error generating SQL: {Error}; executionId: {ExecutionId}", errorBody?.Error, executionId);
                    return errorBody ?? new GenerateSqlResponse("", "Unknown error from agent service");
                }

                return await resp.Content.ReadFromJsonAsync<GenerateSqlResponse>();
            }
            catch (TaskCanceledException tcex)
            {
                _logger.LogError("Request to generate SQL was canceled: {Error}; executionId: {ExecutionId}", tcex.Message, executionId);
                return new GenerateSqlResponse("", "Request was canceled");
            }
            catch (HttpRequestException httpex)
            {
                _logger.LogError("Error occurred while requesting SQL generation: {Error}; executionId: {ExecutionId}", httpex.Message, executionId);
                return new GenerateSqlResponse("", "Error occurred while requesting SQL generation");
            }
            catch (Exception ex)
            {
                _logger.LogError("An unexpected error occurred: {Error}; executionId: {ExecutionId}", ex.Message, executionId);
                return new GenerateSqlResponse("", "An unexpected error occurred");
            }
        }
    }
}
