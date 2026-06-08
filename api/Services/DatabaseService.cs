using Dapper;
using DbAgent.Api.Common;
using Npgsql;

namespace DbAgent.Api.Services;
public class DatabaseService: IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
        _logger = logger;
    }

    public async Task<bool> InsertIntoQueryAttempt(string originalCmd, string generatedSql, Guid id)
    {
        string query = @"
            INSERT INTO query_executions 
                (id, original_command, generated_sql, status)
            VALUES (@id, @originalCmd, @generatedSql, 'processing');
        ";

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            int rowsAffected = await connection.ExecuteAsync(query, new { id, originalCmd, generatedSql });

            if (rowsAffected > 0)
                return true;
            else
                return false;
        }
        catch (Exception ex) 
        {
            _logger.LogError(ex, "Error inserting query attempt into database. ExecutionId: {ExecutionId}", id);
            return false;
        }
    }
}
