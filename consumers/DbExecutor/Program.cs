using Serilog;
using DbAgent.DbExecutor;
using DbAgent.DbExecutor.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dbexecutor-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSingleton<DatabaseService>();
    builder.Services.AddHostedService<Worker>();
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