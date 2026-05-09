using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
//   4. Extract text via PdfPig (fallback to Azure OCR if empty)
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
        // Azure AI Document Intelligence. If not configured, PdfPig-only mode.
        var azureEndpoint = configuration.GetValue<string>("Azure:DocumentIntelligence:Endpoint");
        var azureApiKey = configuration.GetValue<string>("Azure:DocumentIntelligence:ApiKey");
        if (!string.IsNullOrEmpty(azureEndpoint) && !string.IsNullOrEmpty(azureApiKey))
        {
            services.AddSingleton<IOCRService>(sp =>
                new AzureOcrService(sp.GetRequiredService<ILogger<AzureOcrService>>(), azureEndpoint, azureApiKey));
        }
        else
        {
            services.AddSingleton<IOCRService>(sp =>
                new AzureOcrService(sp.GetRequiredService<ILogger<AzureOcrService>>(), null, null));
        }

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

// Auto-create database tables (Dev only)
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

await host.RunAsync();