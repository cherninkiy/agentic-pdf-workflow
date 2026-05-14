using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ApiGateway.Authentication;

/// <summary>
/// Extension methods for configuring JWT authentication in the API Gateway.
///
/// Supports two modes:
///   1. Production — validates tokens against an external identity provider (Jwt:Authority).
///   2. Development — uses a self-signed symmetric key (Jwt:SecretKey) for the dev token endpoint.
///
/// In the Testing environment, authentication is skipped entirely to avoid
/// blocking unit/integration tests.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Adds JWT Bearer authentication and authorization to the service collection.
    /// Skips auth registration in the Testing environment.
    /// </summary>
    public static IServiceCollection AddGatewayAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
            return services; // No auth in unit tests

        var jwtSecret = configuration.GetValue<string>("Jwt:SecretKey");
        var jwtAuthority = configuration.GetValue<string>("Jwt:Authority");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // If an external authority is configured, use it for token validation
                if (!string.IsNullOrWhiteSpace(jwtAuthority))
                {
                    options.Authority = jwtAuthority;
                    options.Audience = configuration.GetValue<string>("Jwt:Audience") ?? "pdf-api-gateway";
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                }
                // Otherwise use symmetric key validation (development mode)
                else if (!string.IsNullOrWhiteSpace(jwtSecret))
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            System.Text.Encoding.UTF8.GetBytes(jwtSecret)),
                        ClockSkew = TimeSpan.Zero
                    };
                }

                options.TokenValidationParameters.ValidateIssuer = !string.IsNullOrWhiteSpace(jwtAuthority);
                options.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(jwtAuthority);
            });

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Adds authentication and authorization middleware to the application pipeline.
    /// Skipped in the Testing environment.
    /// </summary>
    public static IApplicationBuilder UseGatewayAuthentication(this IApplicationBuilder app, IHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
            return app;

        app.UseAuthentication();
        app.UseAuthorization();

        return app;
    }
}