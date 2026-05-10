using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;
using Shared.Interfaces;
using Worker.Consumers;
using Worker.Data;
using Worker.Services;
using Worker.Storage;

// ── Worker Host ──
// Console application running as a generic host with MassTransit consumer.
// Processes PdfProcessingCommand messages from RabbitMQ:
//   1. Idempotency check (processed_messages table)
//   2. Optimistic lock (UPDATE documents SET status='processing' WHERE status='uploaded')
//   3. Download PDF from storage
//   4. Extract text via PdfPig (fallback to Tesseract OCR if empty)
//   5. Save text + mark message processed in one transaction
//   6. ACK on success, throw on failure → MassTransit retry with delays

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var configuration = hostContext.Configuration;

        // ── Database (PostgreSQL via EF Core) ──
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

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
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .Build();

// ── Prometheus metrics server ──
// Serves /metrics on a separate port so Prometheus can scrape worker metrics
var metricServer = new MetricServer(port: 5091);
metricServer.Start();

// Auto-create database tables (Dev only)
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

await host.RunAsync();
