using ApiGateway.BackgroundServices;
using ApiGateway.Data;
using ApiGateway.Extensions;
using ApiGateway.HealthChecks;
using ApiGateway.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Prometheus;

// ------------------------------------------------------------
// Program.cs – Application entry point
// ------------------------------------------------------------
// This file wires up the entire API Gateway workflow:
//   1. Configures the database (PostgreSQL in production, in‑memory for tests).
//   2. Sets up MassTransit with RabbitMQ (skipped in Development to avoid external deps).
//   3. Registers application services, including the DocumentService and the OutboxPublisher background service.
//   4. Adds controllers and Swagger for API documentation.
//   5. Ensures the database schema is created on startup.
// The workflow follows the transactional outbox pattern: uploads are stored in the DB and an outbox row is created; the OutboxPublisher later publishes the message to RabbitMQ.
var builder = WebApplication.CreateBuilder(args);

        // ── Database (PostgreSQL via EF Core) ──
        // Use an in‑memory database for Development and Testing environments to avoid external dependencies.
        // In other environments (e.g., Production) use PostgreSQL when a connection string is provided.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }
        else if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString));
        }
        else
        {
            // Fallback to in‑memory if no connection string is provided.
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }

// ── MassTransit + RabbitMQ ──
// Publishes PdfProcessingCommand messages. The OutboxPublisher
// background service handles reliable delivery via the outbox table.
// Add MassTransit only when RabbitMQ configuration is present (skip in unit tests)
var rabbitHost = builder.Configuration.GetValue<string>("RabbitMq:Host");
// Skip MassTransit in Development (unit test) environment to avoid external RabbitMQ dependency.
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsDevelopment())
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
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsDevelopment())
{
    builder.Services.AddHealthChecks()
        .AddCheck<RabbitMqHealthCheck>("rabbitmq");
}

// ── Application Services ──
// DocumentService orchestrates upload → outbox → file storage.
// OutboxPublisher polls unprocessed outbox rows every 5s and publishes via MassTransit.
builder.Services.AddApplicationServices(builder.Configuration);
builder.Services.AddScoped<DocumentService>();
// Register the OutboxPublisher only when MassTransit (RabbitMQ) is configured and not in Development.
if (!string.IsNullOrWhiteSpace(rabbitHost) && !builder.Environment.IsDevelopment())
{
    builder.Services.AddHostedService<OutboxPublisher>();
}

// ── Controllers + Swagger ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Auto-create database tables (Dev only) ──
// Uses init.sql in PostgreSQL's docker-entrypoint-initdb.d for production-like setup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

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
