using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Exceptions;
using Shared.Interfaces;
using Shared.Models;
using Worker.Agents;
using Worker.Consumers;
using Worker.Services;

namespace Worker.UnitTests;

/// <summary>
/// Tests for PdfProcessingConsumer retry exhaustion and DLQ behavior.
///
/// Uses a real DocumentProcessingAgent with mocked dependencies to verify
/// the consumer's behavior when agent processing fails.
/// </summary>
public class PdfProcessingConsumerRetryTests
{
    private readonly Mock<ICheckpointStore> _checkpointStoreMock;
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<IFileStorage> _fileStorageMock;
    private readonly Mock<ILogger<PdfProcessingConsumer>> _loggerMock;
    private readonly PdfProcessingConsumer _consumer;

    public PdfProcessingConsumerRetryTests()
    {
        _checkpointStoreMock = new Mock<ICheckpointStore>();
        _repositoryMock = new Mock<IDocumentRepository>();
        _fileStorageMock = new Mock<IFileStorage>();
        _loggerMock = new Mock<ILogger<PdfProcessingConsumer>>();

        // Create a real DocumentProcessingAgent with mocked dependencies
        var extractorLoggerMock = new Mock<ILogger<PdfTextExtractor>>();
        var ocrServiceMock = Mock.Of<IOCRService>();
        var textExtractor = new PdfTextExtractor(extractorLoggerMock.Object, ocrServiceMock);

        var agentLoggerMock = new Mock<ILogger<DocumentProcessingAgent>>();
        var agent = new DocumentProcessingAgent(
            textExtractor,
            _repositoryMock.Object,
            _fileStorageMock.Object,
            agentLoggerMock.Object);

        _consumer = new PdfProcessingConsumer(
            agent,
            _checkpointStoreMock.Object,
            _repositoryMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Consume_AgentFailure_RethrowsOriginalException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = Guid.NewGuid(),
            FilePath = "/test.pdf",
            RetryCount = 0
        };

        var consumeContextMock = new Mock<ConsumeContext<PdfProcessingCommand>>();
        consumeContextMock.Setup(x => x.Message).Returns(command);
        consumeContextMock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        // No existing checkpoints
        _checkpointStoreMock
            .Setup(x => x.LoadCompletedCheckpointsAsync(
                "DocumentProcessing", documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowCheckpoint>());

        // File storage throws — triggers agent failure, which is re-thrown by consumer
        _fileStorageMock
            .Setup(x => x.GetAsync("/test.pdf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        // Message not already processed
        _repositoryMock
            .Setup(x => x.IsMessageProcessedAsync(command.MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act & Assert: consumer re-throws the original IOException from the agent
        var ex = await Assert.ThrowsAsync<IOException>(
            () => _consumer.Consume(consumeContextMock.Object));

        Assert.Contains("Storage unavailable", ex.Message);
    }

    [Fact]
    public async Task Consume_AgentFailure_SetsDocumentStatusToFailed()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = Guid.NewGuid(),
            FilePath = "/test.pdf",
            RetryCount = 0
        };

        var consumeContextMock = new Mock<ConsumeContext<PdfProcessingCommand>>();
        consumeContextMock.Setup(x => x.Message).Returns(command);
        consumeContextMock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        _checkpointStoreMock
            .Setup(x => x.LoadCompletedCheckpointsAsync(
                "DocumentProcessing", documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowCheckpoint>());

        _fileStorageMock
            .Setup(x => x.GetAsync("/test.pdf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        _repositoryMock
            .Setup(x => x.IsMessageProcessedAsync(command.MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _repositoryMock
            .Setup(x => x.TryUpdateStatusAsync(
                documentId,
                DocumentStatus.Processing,
                DocumentStatus.Failed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act — catch the expected exception, then verify status update
        try
        {
            await _consumer.Consume(consumeContextMock.Object);
        }
        catch (IOException)
        {
            // Expected
        }

        // Assert: status was updated to Failed
        _repositoryMock.Verify(x => x.TryUpdateStatusAsync(
            documentId,
            DocumentStatus.Processing,
            DocumentStatus.Failed,
            It.Is<string?>(msg => msg != null && msg.Contains("Storage unavailable")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_IdempotencyCheck_SkipsDuplicateMessage()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = messageId,
            FilePath = "/test.pdf",
            RetryCount = 1 // This is a retry — message was already processed
        };

        var consumeContextMock = new Mock<ConsumeContext<PdfProcessingCommand>>();
        consumeContextMock.Setup(x => x.Message).Returns(command);
        consumeContextMock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        // Message already processed (idempotency)
        _repositoryMock
            .Setup(x => x.IsMessageProcessedAsync(messageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _consumer.Consume(consumeContextMock.Object);

        // Verify: IsMessageProcessedAsync was called, but file storage was never accessed
        // (processing skipped entirely)
        _fileStorageMock.Verify(x => x.GetAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}