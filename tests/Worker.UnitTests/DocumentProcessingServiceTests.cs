using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;
using Worker.Services;

namespace Worker.UnitTests;

public class DocumentProcessingServiceTests
{
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<IFileStorage> _fileStorageMock;
    private readonly Mock<PdfTextExtractor> _textExtractorMock;
    private readonly DocumentProcessingService _service;

    public DocumentProcessingServiceTests()
    {
        _repositoryMock = new Mock<IDocumentRepository>();
        _fileStorageMock = new Mock<IFileStorage>();
        var loggerMock = new Mock<ILogger<DocumentProcessingService>>();

        // Create PdfTextExtractor with null OCR (PdfPig only mode)
        var extractorLoggerMock = new Mock<ILogger<PdfTextExtractor>>();
        _textExtractorMock = new Mock<PdfTextExtractor>(extractorLoggerMock.Object, (IOCRService?)null)
        {
            CallBase = true
        };

        _service = new DocumentProcessingService(
            _repositoryMock.Object,
            _fileStorageMock.Object,
            _textExtractorMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task ProcessDocumentAsync_Skips_WhenMessageAlreadyProcessed()
    {
        // Arrange
        _repositoryMock.Setup(x => x.IsMessageProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.ProcessDocumentAsync(Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.True(result);
        _repositoryMock.Verify(x => x.TryUpdateStatusAsync(It.IsAny<Guid>(), It.IsAny<DocumentStatus>(), It.IsAny<DocumentStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessDocumentAsync_Skips_WhenOptimisticLockFails()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        _repositoryMock.Setup(x => x.IsMessageProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _repositoryMock.Setup(x => x.TryUpdateStatusAsync(documentId, DocumentStatus.Uploaded, DocumentStatus.Processing, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Another worker already claimed it

        // Act
        var result = await _service.ProcessDocumentAsync(documentId, Guid.NewGuid());

        // Assert
        Assert.True(result); // Not an error — just skip
    }

    [Fact]
    public async Task ProcessDocumentAsync_SetsFailed_OnException()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        _repositoryMock.Setup(x => x.IsMessageProcessedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _repositoryMock.Setup(x => x.TryUpdateStatusAsync(documentId, DocumentStatus.Uploaded, DocumentStatus.Processing, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repositoryMock.Setup(x => x.GetByIdAsync(documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentDto { Id = documentId, FilePath = "/some/path.pdf" });
        _fileStorageMock.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        // Act
        var result = await _service.ProcessDocumentAsync(documentId, Guid.NewGuid());

        // Assert
        Assert.False(result);
        _repositoryMock.Verify(x => x.TryUpdateStatusAsync(documentId, DocumentStatus.Processing, DocumentStatus.Failed, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}