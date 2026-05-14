using ApiGateway.BackgroundServices;
using ApiGateway.Data;
using ApiGateway.Extensions;
using ApiGateway.HealthChecks;
using ApiGateway.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Prometheus;
using Scalar.AspNetCore;

// ------------------------------------------------------------
// Program.cs – Application entry point
// ------------------------------------------------------------
// This file wires up the entire API Gateway workflow:
//   1. Configures the database via AddDatabase() extension (PostgreSQL or in-memory).
//   2. Sets up MassTransit with RabbitMQ (skipped in Development to avoid external deps).
//   3. Registers application services, including the DocumentService and the OutboxPublisher background service.
//   4. Adds controllers and Swagger for API documentation.
//   5. Ensures the database schema is created on startup.
// The workflow follows the transactional outbox pattern: uploads are stored in the DB and an outbox row is created; the OutboxPublisher later publishes the message to RabbitMQ.
var builder = WebApplication.CreateBuilder(args);

        // ── Database (PostgreSQL via EF Core) ──
        // Delegated to AddDatabase() extension method for SRP compliance.
        // See ServiceCollectionExtensions.AddDatabase() for logic.
        builder.Services.AddDatabase(builder.Configuration, builder.Environment);

// ── MassTransit + RabbitMQ ──
// Publishes PdfProcessingCommand messages. The OutboxPublisher
// background service handles reliable delivery via the outbox table.
// Add MassTransit only when RabbitMQ host is configured.
// Skip in unit tests (Testing environment) to avoid external dependencies.
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

// ── Health Checks ──
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("postgres");
// RabbitMQ health check (custom) — only when configured
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHealthChecks()
        .AddCheck<RabbitMqHealthCheck>("rabbitmq");
}

// ── Application Services ──
// DocumentService orchestrates upload → outbox → file storage.
// OutboxPublisher polls unprocessed outbox rows every 5s and publishes via MassTransit.
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddScoped<DocumentService>();
// Register the OutboxPublisher only when RabbitMQ is configured (not in unit tests).
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<OutboxPublisher>();
}

// ── Controllers + OpenAPI ──
// Using Microsoft.AspNetCore.OpenApi (built-in for .NET 10) instead of Swashbuckle.
// Scalar.AspNetCore provides the API explorer UI at /scalar.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Auto-create database tables (Dev only) ──
// Uses init.sql in PostgreSQL's docker-entrypoint-initdb.d for production-like setup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ── OpenAPI endpoint + Scalar UI ──
// /openapi/v1.json — OpenAPI 3.1 specification
// /scalar — interactive API documentation (modern Swagger UI alternative)
app.MapOpenApi();
app.MapScalarApiReference();

// Prometheus metrics — must be before MapControllers to capture request metrics
app.UseHttpMetrics();

app.MapControllers();

// Health check endpoints
// /health/live: quick probe, runs no dependency checks
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
// /health/ready: checks all dependencies (Postgres, RabbitMQ)
app.MapHealthChecks("/health/ready");

app.MapMetrics();

app.Run();

// Exposed for integration testing with WebApplicationFactory
public partial class Program { }
