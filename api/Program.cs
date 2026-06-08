using Serilog;
using DbAgent.Api.Kafka;
using DbAgent.Api.Common;
using DbAgent.Api.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dbagent-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();
    builder.Services.AddControllers();
    // Add Swagger services for API testing
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IDatabaseService, DatabaseService>();
    builder.Services.AddSingleton<KafkaProducer>();

    var app = builder.Build();

    // Enable middleware to serve generated Swagger as JSON endpoint and the Swagger UI
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "DbAgent API V1"));

    app.UseHttpsRedirection();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application failed to start");
}
finally
{
    Log.CloseAndFlush();
}