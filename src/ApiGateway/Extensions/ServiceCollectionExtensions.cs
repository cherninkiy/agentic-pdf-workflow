using ApiGateway.Data;
using ApiGateway.Storage;
using Shared.Interfaces;

namespace ApiGateway.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var storageProvider = configuration.GetValue<string>("Storage__Provider") ?? "local";
        var localPath = configuration.GetValue<string>("Storage__LocalPath") ?? "/app/storage";

        if (storageProvider == "local")
        {
            services.AddSingleton<IFileStorage>(new LocalFileStorage(localPath));
        }

        services.AddScoped<IDocumentRepository, DocumentRepository>();

        return services;
    }
}