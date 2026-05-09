using System.Text.Json;
using MassTransit;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.BackgroundServices;

/// <summary>
/// Background service that implements the Transactional Outbox pattern.
/// Polls the outbox table every 5s, publishes pending messages to RabbitMQ via MassTransit,
/// then marks them as processed. This guarantees at-least-once delivery without
/// relying on a distributed transaction between the database and message broker.
///
/// Workflow:
///   1. POST /upload → document + outbox row saved in same DB transaction
///   2. OutboxPublisher picks up unprocessed rows (processed_at IS NULL)
///   3. Deserializes JSON payload → publishes to RabbitMQ
///   4. Marks row as processed → won't be picked up again
/// </summary>
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
                        // Deserialize payload — handle corrupt messages gracefully to avoid blocking the queue
                        PdfProcessingCommand? command = null;
                        try
                        {
                            command = JsonSerializer.Deserialize<PdfProcessingCommand>(message.MessagePayload);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, "Corrupt outbox message payload for {OutboxId}, marking as processed to unblock queue", message.Id);
                            await repository.MarkOutboxProcessedAsync(message.Id, stoppingToken);
                            continue;
                        }

                        if (command != null)
                        {
                            await _bus.Publish(command, stoppingToken);
                            _logger.LogInformation("Published outbox message {OutboxId} for document {DocumentId}", message.Id, message.DocumentId);
                        }

                        // Mark as processed so it won't be picked up again
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

            // Poll interval — balance between latency (shorter) and DB load (longer)
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("OutboxPublisher background service stopped");
    }
}