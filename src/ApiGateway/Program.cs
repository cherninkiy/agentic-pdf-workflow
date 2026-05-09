using ApiGateway.BackgroundServices;
using ApiGateway.Data;
using ApiGateway.Extensions;
using ApiGateway.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;

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
        // Use PostgreSQL whenever a connection string is supplied, regardless of environment.
        // Fall back to an in‑memory database only when no connection string is available (e.g., unit tests).
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString));
        }
        else
        {
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

app.MapControllers();

app.Run();

// Exposed for integration testing with WebApplicationFactory
public partial class Program { }
