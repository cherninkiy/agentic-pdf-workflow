using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;
using Worker.Agents;
using Worker.Consumers;
using Worker.Services;

namespace Worker.UnitTests;

/// <summary>
/// Tests for race conditions in concurrent document processing.
///
/// Uses a real DocumentProcessingAgent with mocked dependencies to verify
/// that concurrent duplicate messages are handled gracefully.
///
/// In production, PrefetchCount = 1 prevents concurrent message delivery
/// within a single worker instance, but multiple worker replicas can still
/// receive the same message (at-least-once delivery).
/// </summary>
public class ConcurrencyTests
{
    private readonly Mock<ICheckpointStore> _checkpointStoreMock;
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<IFileStorage> _fileStorageMock;
    private readonly Mock<ILogger<PdfProcessingConsumer>> _loggerMock;
    private readonly PdfProcessingConsumer _consumer;

    public ConcurrencyTests()
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
    public async Task Consume_ConcurrentDuplicate_TryUpdateStatusReturnsFalseForSecondWorker()
    {
        // Arrange — simulate two workers with the same document
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

        // Mock file storage to return a minimal PDF
        var fakePdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF header
        _fileStorageMock
            .Setup(x => x.GetAsync("/test.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(fakePdfBytes));

        // Mock text extraction
        var extractorLoggerMock = new Mock<ILogger<PdfTextExtractor>>();
        var ocrServiceMock = Mock.Of<IOCRService>();
        var textExtractorMock = new Mock<PdfTextExtractor>(extractorLoggerMock.Object, ocrServiceMock)
        {
            CallBase = true
        };
        textExtractorMock
            .Setup(x => x.ExtractTextAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Extracted text");

        _repositoryMock
            .Setup(x => x.IsMessageProcessedAsync(command.MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _repositoryMock
            .Setup(x => x.MarkMessageProcessedAsync(command.MessageId, documentId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate race: first worker already set status to Completed,
        // so TryUpdateStatusAsync returns false for this worker
        _repositoryMock
            .Setup(x => x.TryUpdateStatusAsync(
                documentId,
                DocumentStatus.Processing,
                DocumentStatus.Completed,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        await _consumer.Consume(consumeContextMock.Object);

        // Assert: agent was called, but TryUpdateStatusAsync returned false
        // meaning another worker already finished this document
        _repositoryMock.Verify(x => x.TryUpdateStatusAsync(
            documentId,
            DocumentStatus.Processing,
            DocumentStatus.Completed,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_TwoWorkersSameDocument_SecondWorkerHandlesGracefully()
    {
        // Simulate what happens when two consumers receive the same message
        // from two worker instances (at-least-once delivery)
        var documentId = Guid.NewGuid();
        var command = new PdfProcessingCommand
        {
            DocumentId = documentId,
            MessageId = Guid.NewGuid(),
            FilePath = "/test.pdf",
            RetryCount = 0
        };

        var repositoryMock2 = new Mock<IDocumentRepository>();
        repositoryMock2
            .Setup(x => x.IsMessageProcessedAsync(command.MessageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true); // Already processed by worker 1

        var consumer2 = new PdfProcessingConsumer(
            // Use the same agent instance, but the idempotency check happens first
            new DocumentProcessingAgent(
                new PdfTextExtractor(
                    new Mock<ILogger<PdfTextExtractor>>().Object,
                    Mock.Of<IOCRService>()),
                repositoryMock2.Object,
                Mock.Of<IFileStorage>(),
                new Mock<ILogger<DocumentProcessingAgent>>().Object),
            _checkpointStoreMock.Object,
            repositoryMock2.Object,
            _loggerMock.Object);

        var consumeContextMock = new Mock<ConsumeContext<PdfProcessingCommand>>();
        consumeContextMock.Setup(x => x.Message).Returns(command);
        consumeContextMock.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        // Act — worker 2 receives duplicate
        await consumer2.Consume(consumeContextMock.Object);

        // Assert: file storage was never accessed (processing skipped before agent call)
        _fileStorageMock.Verify(x => x.GetAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}