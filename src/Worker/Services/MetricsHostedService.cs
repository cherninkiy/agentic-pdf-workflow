using Microsoft.Extensions.Configuration;
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
    private readonly int _port;

    public MetricsHostedService(IConfiguration configuration, ILogger<MetricsHostedService> logger)
    {
        _logger = logger;
        _port = configuration.GetValue<int>("Metrics:Port", 5091);
        _metricServer = new MetricServer(port: _port);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Prometheus metric server on port {Port}", _port);
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