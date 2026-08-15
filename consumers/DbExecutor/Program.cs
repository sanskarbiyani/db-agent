using Serilog;
using DbAgent.DbExecutor;
using DbAgent.DbExecutor.Services;
using DbAgent.DbExecutor.Interfaces;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dbexecutor-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
    builder.Services.AddSingleton<RetryChannel>();

    builder.Services.AddHostedService<Worker>();
    builder.Services.AddHostedService<RetryQueryConsumer>();
    builder.Services.AddHostedService<RetryChannelProcessor>();

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