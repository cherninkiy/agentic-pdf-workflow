using Prometheus;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.Services;

public class DocumentService
{
    private static readonly Counter UploadCount = Metrics
        .CreateCounter("document_upload_total", "Total number of uploaded documents.");

    private readonly IDocumentRepository _repository;
    private readonly IOutboxRepository _outboxRepository;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        IDocumentRepository repository,
        IOutboxRepository outboxRepository,
        IFileStorage fileStorage,
        ILogger<DocumentService> logger)
    {
        _repository = repository;
        _outboxRepository = outboxRepository;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    public async Task<UploadResponse> CreateDocumentAsync(Stream fileStream, string filename, CancellationToken cancellationToken = default)
    {
        var documentId = Guid.NewGuid();
        var fileExtension = Path.GetExtension(filename);
        var storedFileName = $"{documentId}{fileExtension}";
        var filePath = await _fileStorage.SaveAsync(fileStream, storedFileName, cancellationToken);

        var document = new DocumentDto
        {
            Id = documentId,
            Filename = filename,
            Status = DocumentStatus.Uploaded,
            FilePath = filePath,
            CreatedAt = DateTime.UtcNow
        };

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            MessagePayload = System.Text.Json.JsonSerializer.Serialize(new PdfProcessingCommand
            {
                DocumentId = documentId,
                FilePath = filePath,
                MessageId = Guid.NewGuid(),
                RetryCount = 0
            }),
            CreatedAt = DateTime.UtcNow
        };

        await _outboxRepository.CreateAsync(document, outboxMessage, cancellationToken);
        UploadCount.Inc();
        _logger.LogInformation("Document {DocumentId} uploaded, filename: {Filename}, outbox message: {OutboxId}", documentId, filename, outboxMessage.Id);

        return new UploadResponse
        {
            DocumentId = documentId,
            Status = "accepted",
            Message = "Document uploaded successfully and queued for processing"
        };
    }

    public async Task<List<DocumentListItem>> GetAllDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await _repository.GetAllAsync(cancellationToken);
        var ordered = documents
            .OrderByDescending(d => d.CreatedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new DocumentListItem
            {
                Id = d.Id,
                Filename = d.Filename,
                Status = d.Status,
                CreatedAt = d.CreatedAt
            })
            .ToList();
        return ordered;
    }

    public async Task<(DocumentDto? Document, string? ErrorMessage, int? StatusCode)> GetDocumentTextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await _repository.GetByIdAsync(id, cancellationToken);
        if (document == null)
            return (null, "Document not found", 404);

        return document.Status switch
        {
            DocumentStatus.Completed => (document, null, 200),
            DocumentStatus.Processing => (document, "Document is still being processed", 202),
            DocumentStatus.Failed => (document, $"Document processing failed: {document.ErrorMessage}", 409),
            DocumentStatus.Uploaded => (document, "Document is queued for processing", 202),
            _ => (null, "Unknown status", 500)
        };
    }
}