using ApiGateway.Data;
using ApiGateway.Storage;
using Shared.Interfaces;

namespace ApiGateway.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var storageProvider = configuration.GetValue<string>("Storage__Provider") ?? "local";

        // Determine storage path:
        // 1. Explicit config via Storage__LocalPath or Storage:LocalPath
        // 2. /app/storage if running in Docker (directory exists or explicitly configured)
        // 3. Fallback to temp directory for local dev and tests
        var localPath = configuration.GetValue<string>("Storage__LocalPath")
                        ?? configuration.GetValue<string>("Storage:LocalPath");

        if (string.IsNullOrEmpty(localPath) || localPath == "/app/storage" && !Directory.Exists("/app/storage"))
        {
            localPath = Path.GetTempPath();
        }

        if (storageProvider == "local")
        {
            services.AddSingleton<IFileStorage>(sp =>
            {
                var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<LocalFileStorage>>() ??
                             Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalFileStorage>.Instance;
                return new LocalFileStorage(localPath, logger);
            });
        }

        services.AddScoped<IDocumentRepository, DocumentRepository>();

        return services;
    }
}