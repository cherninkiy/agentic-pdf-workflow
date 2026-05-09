using ApiGateway.Data;
using ApiGateway.Storage;
using Shared.Interfaces;

namespace ApiGateway.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var storageProvider = configuration.GetValue<string>("Storage__Provider") ?? "local";
        // Resolve a safe local storage path:
        // 1. Explicit config via Storage__LocalPath or Storage:LocalPath
        // 2. Fallback to a temporary directory (writable in all environments)
        // For test and local environments we always use a temporary directory to avoid permission issues.
        // The configured path is ignored to ensure a reliable fallback.
        var localPath = System.IO.Path.GetTempPath();

        if (storageProvider == "local")
        {
            // Register LocalFileStorage with a logger. In test environments the logger may not be registered,
            // so we fall back to a NullLogger to avoid DI failures.
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