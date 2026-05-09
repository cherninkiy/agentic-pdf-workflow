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
        var configuredPath = configuration.GetValue<string>("Storage__LocalPath") ??
                             configuration.GetValue<string>("Storage:LocalPath");
        var localPath = configuredPath ?? System.IO.Path.GetTempPath();

        if (storageProvider == "local")
        {
            services.AddSingleton<IFileStorage>(new LocalFileStorage(localPath));
        }

        services.AddScoped<IDocumentRepository, DocumentRepository>();

        return services;
    }
}