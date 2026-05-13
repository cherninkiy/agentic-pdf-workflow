using System.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Shared.Interfaces;
using Shared.Models;
using Worker.Agents;
using Worker.Services;

namespace Worker.UnitTests;

/// <summary>
/// Unit tests for DocumentProcessingAgent.
/// Tests the MAF agent workflow orchestration with mocked dependencies.
/// </summary>
public class DocumentProcessingAgentTests
{
    private readonly Mock<IDocumentRepository> _repositoryMock;
    private readonly Mock<IFileStorage> _fileStorageMock;
    private readonly Mock<ICheckpointStore> _checkpointStoreMock;
    private readonly Mock<PdfTextExtractor> _textExtractorMock;
    private readonly Mock<ILogger<DocumentProcessingAgent>> _loggerMock;
    private readonly DocumentProcessingAgent _agent;

    public DocumentProcessingAgentTests()
    {
        _repositoryMock = new Mock<IDocumentRepository>();
        _fileStorageMock = new Mock<IFileStorage>();
        _checkpointStoreMock = new Mock<ICheckpointStore>();
        _loggerMock = new Mock<ILogger<DocumentProcessingAgent>>();

        // Create PdfTextExtractor with mocked OCR (null disables OCR fallback)
        var extractorLoggerMock = new Mock<ILogger<PdfTextExtractor>>();
        _textExtractorMock = new Mock<PdfTextExtractor>(extractorLoggerMock.Object, Mock.Of<IOCRService>())
        {
            CallBase = true
        };

        _agent = new DocumentProcessingAgent(
            _textExtractorMock.Object,
            _repositoryMock.Object,
            _fileStorageMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public void AgentName_ReturnsDocumentProcessing()
    {
        Assert.Equal("DocumentProcessing", _agent.AgentName);
    }

    [Fact]
    public void Activities_ReturnsFiveActivities()
    {
        var activities = _agent.Activities;

        Assert.Equal(5, activities.Count);
        Assert.Equal("DownloadDocument", activities[0]);
        Assert.Equal("ParseDocument", activities[1]);
        Assert.Equal("ExtractText", activities[2]);
        Assert.Equal("SaveResult", activities[3]);
        Assert.Equal("UpdateStatus", activities[4]);
    }

    [Fact]
    public async Task ExecuteAsync_FullWorkflow_CompletesSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var context = new AgentContext
        {
            DocumentId = documentId,
            FilePath = "/test/sample.pdf",
            AgentName = _agent.AgentName,
            CurrentActivity = _agent.Activities.First()
        };

        // No existing checkpoints (first run)
        _checkpointStoreMock
            .Setup(x => x.LoadCompletedCheckpointsAsync(_agent.AgentName, documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowCheckpoint>());

        // Mock file storage — return a minimal PDF-like byte array
        var fakePdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF header
        var memoryStream = new MemoryStream(fakePdfBytes);
        _fileStorageMock
            .Setup(x => x.GetAsync("/test/sample.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(memoryStream);

        // Mock text extraction — return some text
        _textExtractorMock
            .Setup(x => x.ExtractTextAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Extracted text from PDF");

        // Mock repository
        _repositoryMock
            .Setup(x => x.UpdateTextAsync(documentId, "Extracted text from PDF", DocumentStatus.Processing, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(x => x.TryUpdateStatusAsync(documentId, DocumentStatus.Processing, DocumentStatus.Completed, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _agent.ExecuteAsync(context, _checkpointStoreMock.Object);

        // Assert
        Assert.True(result.IsSuccess);

        // Verify all 5 checkpoints were saved
        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "DownloadDocument",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "ParseDocument",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "ExtractText",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "SaveResult",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "UpdateStatus",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        // Verify cleanup
        _checkpointStoreMock.Verify(x => x.DeleteCheckpointsAsync(
            _agent.AgentName, documentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ResumeAfterCrash_SkipsCompletedActivities()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var context = new AgentContext
        {
            DocumentId = documentId,
            FilePath = "/test/sample.pdf",
            AgentName = _agent.AgentName,
            CurrentActivity = _agent.Activities.First()
        };

        // Simulate crash after DownloadDocument and ParseDocument completed
        var completedCheckpoints = new List<WorkflowCheckpoint>
        {
            new() { AgentName = "DocumentProcessing", DocumentId = documentId, CurrentActivity = "DownloadDocument", IsCompleted = true, StateData = Convert.ToBase64String(new byte[] { 0x25, 0x50, 0x44, 0x46 }) },
            new() { AgentName = "DocumentProcessing", DocumentId = documentId, CurrentActivity = "ParseDocument", IsCompleted = true, StateData = "Parsed text" }
        };

        _checkpointStoreMock
            .Setup(x => x.LoadCompletedCheckpointsAsync(_agent.AgentName, documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedCheckpoints);

        // Mock text extraction — should use parsed text from checkpoint
        _textExtractorMock
            .Setup(x => x.ExtractTextAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Parsed text");

        _repositoryMock
            .Setup(x => x.UpdateTextAsync(documentId, It.IsAny<string>(), DocumentStatus.Processing, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _repositoryMock
            .Setup(x => x.TryUpdateStatusAsync(documentId, DocumentStatus.Processing, DocumentStatus.Completed, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _agent.ExecuteAsync(context, _checkpointStoreMock.Object);

        // Assert
        Assert.True(result.IsSuccess);

        // Verify DownloadDocument and ParseDocument checkpoints were NOT saved again
        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "DownloadDocument",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Never);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "ParseDocument",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify remaining activities were executed
        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "ExtractText",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "SaveResult",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);

        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "UpdateStatus",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_Failure_SavesFailureCheckpoint()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var context = new AgentContext
        {
            DocumentId = documentId,
            FilePath = "/test/sample.pdf",
            AgentName = _agent.AgentName,
            CurrentActivity = _agent.Activities.First()
        };

        _checkpointStoreMock
            .Setup(x => x.LoadCompletedCheckpointsAsync(_agent.AgentName, documentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowCheckpoint>());

        // Mock file storage — throw exception on download
        _fileStorageMock
            .Setup(x => x.GetAsync("/test/sample.pdf", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<IOException>(
            () => _agent.ExecuteAsync(context, _checkpointStoreMock.Object));

        Assert.Contains("Storage unavailable", ex.Message);

        // Verify failure checkpoint was saved
        _checkpointStoreMock.Verify(x => x.SaveCheckpointAsync(
            _agent.AgentName, documentId, "Failure",
            It.IsAny<AgentResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}