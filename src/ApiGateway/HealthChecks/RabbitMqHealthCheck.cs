using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGateway.HealthChecks;

/// <summary>
/// Health check for RabbitMQ using rabbitmq-diagnostics CLI.
/// Returns Healthy if the broker is accepting connections.
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "rabbitmq-diagnostics",
                    Arguments = "check_port_connectivity",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0
                ? HealthCheckResult.Healthy("RabbitMQ is accepting connections")
                : HealthCheckResult.Unhealthy($"RabbitMQ diagnostics exit code: {process.ExitCode}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ health check failed", ex);
        }
    }
}