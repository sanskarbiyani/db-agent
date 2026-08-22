using DbAgent.DbExecutor;
using DbAgent.DbExecutor.Interfaces;
using DbAgent.DbExecutor.logs;
using DbAgent.DbExecutor.Services;
using Serilog;
using System.Text.Json;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dbexecutor-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
    builder.Services.AddSingleton<RetryChannel>();
    builder.Services.AddHttpClient<IFixAgentClient, FixAgentClient>(client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["SQLAgent:BaseUrl"] ?? "http://localhost:8000");
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; // Python style naming
    });

    builder.Services.AddHostedService<PendingQueryConsumer>();
    builder.Services.AddHostedService<RetryQueryConsumer>();
    builder.Services.AddHostedService<RetryChannelProcessor>();
    builder.Services.AddHostedService<FixableSchemaConsumer>();

    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "DbExecutor failed to start");
}
finally
{
    Log.CloseAndFlush();
}