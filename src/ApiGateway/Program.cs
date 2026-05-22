using ApiGateway.Authentication;
using ApiGateway.BackgroundServices;
using ApiGateway.Data;
using ApiGateway.Extensions;
using ApiGateway.HealthChecks;
using ApiGateway.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(context.Configuration)
                .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

builder.Services.AddDatabase(builder.Configuration, builder.Environment);

var rabbitHost = builder.Configuration.GetValue<string>("RabbitMq:Host");
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(rabbitHost, h =>
            {
                h.Username(builder.Configuration.GetValue<string>("RabbitMq:Username") ?? "guest");
                h.Password(builder.Configuration.GetValue<string>("RabbitMq:Password") ?? "guest");
            });

            cfg.ConfigureEndpoints(context);
        });
    });
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<GatewayDbContext>("postgres");

if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHealthChecks()
        .AddCheck<RabbitMqHealthCheck>("rabbitmq");
}

builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddScoped<DocumentService>();

if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<OutboxPublisher>();
}

builder.Services.AddGatewayAuthentication(builder.Configuration, builder.Environment);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
    db.Database.EnsureCreated();
}

app.MapOpenApi();
app.MapScalarApiReference();

app.UseGatewayAuthentication(app.Environment);

app.UseHttpMetrics();

app.MapControllers();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready");

app.MapMetrics();

app.Run();

public partial class Program { }