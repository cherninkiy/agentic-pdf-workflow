using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Shared.Interfaces;
using Worker.Agents;
using Worker.Consumers;
using Worker.Data;
using Worker.Services;
using Worker.Storage;

// ── Worker Host ──
// Console application running as a generic host with MassTransit consumer.
// Uses hybrid architecture:
//   - MassTransit handles message delivery, retry, DLQ (inter-service boundary)
//   - MAF DocumentProcessingAgent handles workflow orchestration with checkpoints
//
// Processing workflow (inside MAF agent):
//   1. DownloadDocument  — download PDF from storage
//   2. ParseDocument     — extract text via PdfPig
//   3. ExtractText       — OCR fallback via Tesseract
//   4. SaveResult        — save text to database
//   5. UpdateStatus      — mark document as completed
//
// Each activity saves a checkpoint. If worker crashes, agent resumes from last checkpoint.

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, loggerConfig) =>
        loggerConfig.ReadFrom.Configuration(context.Configuration)
                    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()))
    .ConfigureServices((hostContext, services) =>
    {
        var configuration = hostContext.Configuration;

        // ── Database (PostgreSQL via EF Core) ──
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection"))
                   .UseSnakeCaseNamingConvention());

        // ── Repository ──
        services.AddScoped<IDocumentRepository, DocumentRepository>();

        // ── File Storage ──
        // Uses local Docker volume shared with ApiGateway for MVP.
        var localPath = configuration.GetValue<string>("Storage:LocalPath") ?? "/app/storage";
        services.AddSingleton<IFileStorage>(new LocalFileStorage(localPath));

        // ── OCR Service (optional) ──
        // Tesseract OCR — used as fallback when PdfPig returns no text (scanned PDFs).
        // Requires tesseract-ocr and poppler-utils installed on the system.
        // PdfTextExtractor gracefully skips OCR fallback when IOCRService is not registered.
        // Enabled by default — remove or comment out to use PdfPig-only mode.
        services.AddSingleton<IOCRService>(sp =>
            new TesseractOcrService(sp.GetRequiredService<ILogger<TesseractOcrService>>()));

        // ── Application Services ──
        services.AddScoped<PdfTextExtractor>();

        // ── Prometheus metrics as a hosted service ──
        // Wraps MetricServer in IHostedService so it stops gracefully on SIGTERM
        services.AddHostedService<MetricsHostedService>();

        // ── MAF Agent ──
        // DocumentProcessingAgent orchestrates the PDF processing workflow
        // with checkpoint-based durability. Registered as scoped so each
        // message gets a fresh agent instance with its own state.
        services.AddScoped<DocumentProcessingAgent>();

        // ── Checkpoint Store ──
        // PostgreSQL-backed checkpoint storage for durable agent execution.
        // Enables resume after worker crash — agent continues from last checkpoint.
        services.AddScoped<ICheckpointStore, PostgreSqlCheckpointStore>();

        // Keep DocumentProcessingService for backward compatibility
        // (can be removed in future iterations)
        services.AddScoped<DocumentProcessingService>();

        // ── MassTransit + RabbitMQ Consumer ──
        // Retry policy matches ADR-001 Section 5: 5s → 30s → 60s delays.
        // After 3 retries, message goes to error queue (DLQ).
        services.AddMassTransit(x =>
        {
            x.AddConsumer<PdfProcessingConsumer>(configurator =>
            {
                configurator.UseMessageRetry(r =>
                {
                    r.Intervals(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
                    r.Ignore<ArgumentNullException>();
                });
            });

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(configuration.GetValue<string>("RabbitMq:Host") ?? "localhost", h =>
                {
                    h.Username(configuration.GetValue<string>("RabbitMq:Username") ?? "guest");
                    h.Password(configuration.GetValue<string>("RabbitMq:Password") ?? "guest");
                });

                cfg.ReceiveEndpoint("pdf_processing", e =>
                {
                    e.PrefetchCount = 1; // One message at a time per worker instance
                    e.ConfigureConsumer<PdfProcessingConsumer>(context);
                });
            });
        });
    })
    .Build();

// Auto-create database tables (Dev only)
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

await host.RunAsync();
