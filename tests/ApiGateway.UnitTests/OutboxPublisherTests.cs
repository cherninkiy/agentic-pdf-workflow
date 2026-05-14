using System.Text.Json;
using ApiGateway.BackgroundServices;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.UnitTests;

/// <summary>
/// Unit tests for the OutboxPublisher background service.
///
/// The OutboxPublisher implements the transactional outbox pattern:
/// it polls the outbox table, publishes pending messages via MassTransit,
/// then marks them as processed. These tests verify its reliability
/// under various failure conditions.
/// </summary>
public class OutboxPublisherTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IBus> _busMock;
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<ILogger<OutboxPublisher>> _loggerMock;
    private readonly OutboxPublisher _publisher;

    public OutboxPublisherTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _busMock = new Mock<IBus>();
        _loggerMock = new Mock<ILogger<OutboxPublisher>>();
        _repositoryMock = new Mock<IDocumentRepository>();

        // Set up scope factory to return a scope that provides the repository
        _scopeFactoryMock
            .Setup(x => x.CreateScope())
            .Returns(_scopeMock.Object);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(x => x.GetService(typeof(IDocumentRepository)))
            .Returns(_repositoryMock.Object);

        _scopeMock
            .Setup(x => x.ServiceProvider)
            .Returns(serviceProviderMock.Object);

        _publisher = new OutboxPublisher(
            _scopeFactoryMock.Object,
            _busMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_PublishesPendingMessagesAndMarksProcessed()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = Guid.NewGuid(),
            FilePath = "/test.pdf"
        };

        var pendingMessages = new List<OutboxMessage>
        {
            new()
            {
                Id = outboxId,
                DocumentId = documentId,
                MessagePayload = JsonSerializer.Serialize(command),
                CreatedAt = DateTime.UtcNow
            }
        };

        _repositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        _repositoryMock
            .Setup(x => x.MarkOutboxProcessedAsync(outboxId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Use a cancellation token source to stop the loop after one iteration
        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        // Allow one loop iteration to complete
        await Task.Delay(500);
        await cts.CancelAsync();

        // Assert
        _busMock.Verify(x => x.Publish(
            It.IsAny<PdfProcessingCommand>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _repositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            outboxId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsCorruptMessagesAndContinues()
    {
        // Arrange
        var pendingMessages = new List<OutboxMessage>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DocumentId = Guid.NewGuid(),
                MessagePayload = "not-valid-json",
                CreatedAt = DateTime.UtcNow
            }
        };

        _repositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        _repositoryMock
            .Setup(x => x.MarkOutboxProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        // Assert: corrupt message is marked as processed (unblock queue) but not published
        _busMock.Verify(x => x.Publish(
            It.IsAny<PdfProcessingCommand>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _repositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesOnPublishFailure()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = Guid.NewGuid(),
            FilePath = "/test.pdf"
        };

        var pendingMessages = new List<OutboxMessage>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DocumentId = documentId,
                MessagePayload = JsonSerializer.Serialize(command),
                CreatedAt = DateTime.UtcNow
            }
        };

        _repositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        // Simulate publish failure on first attempt
        _busMock
            .Setup(x => x.Publish(It.IsAny<PdfProcessingCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("RabbitMQ unavailable"));

        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        // Assert: exception is caught, loop continues, message is NOT marked processed
        // (it will be retried on the next poll cycle)
        _repositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        // Arrange
        _repositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxMessage>());

        using var cts = new CancellationTokenSource();

        // Act: start and immediately cancel
        var executeTask = _publisher.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Wait a brief moment for the loop to observe cancellation
        await Task.Delay(200);

        // Assert: no exception thrown, service stops gracefully
        Assert.True(executeTask.IsCompletedSuccessfully);
    }
}