using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ApiGateway.HealthChecks;

/// <summary>
/// Health check for RabbitMQ using TCP connectivity check.
/// Tries to connect to the RabbitMQ port (default 5672) to verify it's reachable.
/// </summary>
public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly string _host;
    private readonly int _port;

    public RabbitMqHealthCheck(string host = "rabbitmq", int port = 5672)
    {
        _host = host;
        _port = port;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_host, _port, cancellationToken);
            return HealthCheckResult.Healthy($"RabbitMQ port {_port} is reachable on {_host}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ health check failed", ex);
        }
    }
}