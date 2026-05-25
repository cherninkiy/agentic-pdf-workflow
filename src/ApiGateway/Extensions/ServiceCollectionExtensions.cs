using ApiGateway.Data;
using ApiGateway.Repositories;
using ApiGateway.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shared.Interfaces;

namespace ApiGateway.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (environment.IsEnvironment("Testing"))
        {
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }
        else if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseNpgsql(connectionString)
                       .UseSnakeCaseNamingConvention());
        }
        else
        {
            services.AddDbContext<GatewayDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration configuration)
    {
        var storageProvider = configuration.GetValue<string>("Storage__Provider") ?? "local";

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

        services.AddScoped<ApiGateway.Repositories.DocumentRepository>();
        services.AddScoped<IDocumentRepository>(sp => sp.GetRequiredService<ApiGateway.Repositories.DocumentRepository>());
        services.AddScoped<IOutboxRepository>(sp => sp.GetRequiredService<ApiGateway.Repositories.DocumentRepository>());

        return services;
    }
}