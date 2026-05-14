using ApiGateway.Data;
using ApiGateway.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Shared.Interfaces;

namespace ApiGateway.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Configures the database provider based on environment and connection string availability.
    ///
    /// - Testing environment → in-memory database (for unit tests)
    /// - Production with connection string → PostgreSQL with snake_case naming
    /// - Fallback (e.g., local dev without PostgreSQL) → in-memory database
    ///
    /// Extracted from Program.cs to comply with SRP — the entry point should
    /// only compose services, not decide which database to use.
    /// </summary>
    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (environment.IsEnvironment("Testing"))
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }
        else if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(connectionString)
                       .UseSnakeCaseNamingConvention());
        }
        else
        {
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase("TestDb"));
        }

        return services;
    }

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