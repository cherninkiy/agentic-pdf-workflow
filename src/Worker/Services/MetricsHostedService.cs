using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Worker.Services;

/// <summary>
/// Hosted service that wraps the Prometheus MetricServer for proper
/// lifecycle integration with the generic host. On SIGTERM, the host
/// calls StopAsync(), which stops the metric server gracefully.
///
/// Without this wrapper, the MetricServer created via "using var" in
/// Program.cs would remain hanging after host shutdown.
/// </summary>
public class MetricsHostedService : IHostedService
{
    private readonly MetricServer _metricServer;
    private readonly ILogger<MetricsHostedService> _logger;

    public MetricsHostedService(ILogger<MetricsHostedService> logger)
    {
        _logger = logger;
        // Prometheus metrics endpoint — separate port for worker metrics
        _metricServer = new MetricServer(port: 5091);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Prometheus metric server on port 5091");
        _metricServer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Prometheus metric server");
        _metricServer.Stop();
        return Task.CompletedTask;
    }
}