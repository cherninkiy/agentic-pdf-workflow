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

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var configuration = hostContext.Configuration;

        // Database
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Repository
        services.AddScoped<IDocumentRepository, DocumentRepository>();

        // File storage
        var localPath = configuration.GetValue<string>("Storage:LocalPath") ?? "/app/storage";
        services.AddSingleton<IFileStorage>(new LocalFileStorage(localPath));

        // OCR service (optional)
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

        // Application services
        services.AddScoped<PdfTextExtractor>();
        services.AddScoped<DocumentProcessingService>();

        // MassTransit with RabbitMQ
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
                    e.PrefetchCount = 1;
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

// Auto-migrate database
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

await host.RunAsync();