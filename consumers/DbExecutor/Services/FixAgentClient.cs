using DbAgent.Common.Models;
using DbAgent.DbExecutor.logs;
using DbAgent.DbExecutor.Models;
using System.Net.Http.Json;

namespace DbAgent.DbExecutor.Services
{
    public class FixAgentClient: IFixAgentClient
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<FixAgentClient> _logger;

        public FixAgentClient(HttpClient httpClient, ILogger<FixAgentClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<PythonAgentResponse?> GetFixSql(FixAgentRequest request, string executionId)
        {
            try
            {
                var resp = await _httpClient.PostAsJsonAsync("fix", request);
                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadFromJsonAsync<PythonAgentResponse>();
                    _logger.LogError("Error fixing SQL: {Error}; executionId: {ExecutionId}", errorBody?.Error, executionId);
                    return errorBody ?? new PythonAgentResponse("", "Unknown error from agent service", null);
                }
                return await resp.Content.ReadFromJsonAsync<PythonAgentResponse>();
            }
            catch (Exception ex)
            {
                _logger.LogError("An unexpected error occurred while fixing SQL: {Error}; executionId: {ExecutionId}", ex.Message, executionId);
                return new PythonAgentResponse("", "An unexpected error occurred while fixing SQL", null);
            }
        }
    }
}
