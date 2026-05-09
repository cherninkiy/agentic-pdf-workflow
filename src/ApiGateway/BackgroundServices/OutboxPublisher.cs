using System.Text.Json;
using MassTransit;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.BackgroundServices;

public class OutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBus _bus;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(IServiceScopeFactory scopeFactory, IBus bus, ILogger<OutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _bus = bus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisher background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
                var pendingMessages = await repository.GetOutboxPendingAsync(stoppingToken);

                foreach (var message in pendingMessages)
                {
                    try
                    {
                        var command = JsonSerializer.Deserialize<PdfProcessingCommand>(message.MessagePayload);
                        if (command != null)
                        {
                            await _bus.Publish(command, stoppingToken);
                            _logger.LogInformation("Published outbox message {OutboxId} for document {DocumentId}", message.Id, message.DocumentId);
                        }

                        await repository.MarkOutboxProcessedAsync(message.Id, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to publish outbox message {OutboxId}", message.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OutboxPublisher loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("OutboxPublisher background service stopped");
    }
}