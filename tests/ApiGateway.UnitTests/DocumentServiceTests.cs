using ApiGateway.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.UnitTests;

public class DocumentServiceTests
{
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<IFileStorage> _fileStorageMock;
    private readonly DocumentService _service;

    public DocumentServiceTests()
    {
        _repositoryMock = new Mock<IDocumentRepository>();
        _fileStorageMock = new Mock<IFileStorage>();
        var loggerMock = new Mock<ILogger<DocumentService>>();
        _service = new DocumentService(_repositoryMock.Object, _fileStorageMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task CreateDocumentAsync_SavesFileAndCreatesOutbox()
    {
        // Arrange
        var filename = "test.pdf";
        var content = new MemoryStream(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // PDF magic bytes
        var expectedPath = $"/storage/{Guid.NewGuid()}.pdf";
        _fileStorageMock.Setup(x => x.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedPath);
        _repositoryMock.Setup(x => x.CreateAsync(It.IsAny<DocumentDto>(), It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.CreateDocumentAsync(content, filename);

        // Assert
        Assert.Equal("accepted", result.Status);
        Assert.NotEqual(Guid.Empty, result.DocumentId);
        _fileStorageMock.Verify(x => x.SaveAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _repositoryMock.Verify(x => x.CreateAsync(It.IsAny<DocumentDto>(), It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAllDocumentsAsync_ReturnsOrderedList()
    {
        // Arrange
        var docs = new List<DocumentDto>
        {
            new() { Id = Guid.NewGuid(), Filename = "a.pdf", Status = DocumentStatus.Completed, CreatedAt = DateTime.UtcNow.AddMinutes(-1) },
            new() { Id = Guid.NewGuid(), Filename = "b.pdf", Status = DocumentStatus.Uploaded, CreatedAt = DateTime.UtcNow }
        };
        _repositoryMock.Setup(x => x.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(docs);

        // Act
        var result = await _service.GetAllDocumentsAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("b.pdf", result[0].Filename); // newest first
        Assert.Equal("a.pdf", result[1].Filename);
    }

    [Fact]
    public async Task GetDocumentTextAsync_ReturnsNotFound_ForMissingId()
    {
        // Arrange
        _repositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DocumentDto?)null);

        // Act
        var (doc, error, statusCode) = await _service.GetDocumentTextAsync(Guid.NewGuid());

        // Assert
        Assert.Null(doc);
        Assert.Equal(404, statusCode);
        Assert.Equal("Document not found", error);
    }

    [Fact]
    public async Task GetDocumentTextAsync_Returns200_ForCompletedDocument()
    {
        // Arrange
        var id = Guid.NewGuid();
        var doc = new DocumentDto
        {
            Id = id,
            Filename = "test.pdf",
            Status = DocumentStatus.Completed,
            ExtractedText = "Hello World"
        };
        _repositoryMock.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(doc);

        // Act
        var (result, error, statusCode) = await _service.GetDocumentTextAsync(id);

        // Assert
        Assert.Equal(200, statusCode);
        Assert.Equal("Hello World", result?.ExtractedText);
        Assert.Null(error);
    }

    [Fact]
    public async Task GetDocumentTextAsync_Returns409_ForFailedDocument()
    {
        // Arrange
        var id = Guid.NewGuid();
        var doc = new DocumentDto
        {
            Id = id,
            Status = DocumentStatus.Failed,
            ErrorMessage = "OCR failed"
        };
        _repositoryMock.Setup(x => x.GetByIdAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(doc);

        // Act
        var (_, error, statusCode) = await _service.GetDocumentTextAsync(id);

        // Assert
        Assert.Equal(409, statusCode);
        Assert.Contains("OCR failed", error);
    }
}