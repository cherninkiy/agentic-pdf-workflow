using System.Text.Json;
using ApiGateway.BackgroundServices;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.UnitTests;

public class OutboxPublisherTests
{
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IBus> _busMock;
    private readonly Mock<IOutboxRepository> _outboxRepositoryMock;
    private readonly Mock<ILogger<OutboxPublisher>> _loggerMock;
    private readonly OutboxPublisher _publisher;

    public OutboxPublisherTests()
    {
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _scopeMock = new Mock<IServiceScope>();
        _busMock = new Mock<IBus>();
        _loggerMock = new Mock<ILogger<OutboxPublisher>>();
        _outboxRepositoryMock = new Mock<IOutboxRepository>();

        _scopeFactoryMock
            .Setup(x => x.CreateScope())
            .Returns(_scopeMock.Object);

        var serviceProviderMock = new Mock<IServiceProvider>();
        serviceProviderMock
            .Setup(x => x.GetService(typeof(IOutboxRepository)))
            .Returns(_outboxRepositoryMock.Object);

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

        _outboxRepositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        _outboxRepositoryMock
            .Setup(x => x.MarkOutboxProcessedAsync(outboxId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        _busMock.Verify(x => x.Publish(
            It.IsAny<PdfProcessingCommand>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _outboxRepositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            outboxId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsCorruptMessagesAndContinues()
    {
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

        _outboxRepositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        _outboxRepositoryMock
            .Setup(x => x.MarkOutboxProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        _busMock.Verify(x => x.Publish(
            It.IsAny<PdfProcessingCommand>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _outboxRepositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesOnPublishFailure()
    {
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

        _outboxRepositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pendingMessages);

        _busMock
            .Setup(x => x.Publish(It.IsAny<PdfProcessingCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("RabbitMQ unavailable"));

        using var cts = new CancellationTokenSource();
        var executeTask = _publisher.StartAsync(cts.Token);

        await Task.Delay(500);
        await cts.CancelAsync();

        _outboxRepositoryMock.Verify(x => x.MarkOutboxProcessedAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        _outboxRepositoryMock
            .Setup(x => x.GetOutboxPendingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<OutboxMessage>());

        using var cts = new CancellationTokenSource();

        var executeTask = _publisher.StartAsync(cts.Token);
        await cts.CancelAsync();

        await Task.Delay(200);

        Assert.True(executeTask.IsCompletedSuccessfully);
    }
}