using Prometheus;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.Services;

// ------------------------------------------------------------
// DocumentService – Core business logic for the API Gateway
// ------------------------------------------------------------
// This service implements the upload flow using the transactional outbox pattern:
//   * Saves the uploaded PDF to the configured IFileStorage implementation.
//   * Persists a DocumentDto with status 'Uploaded'.
//   * Creates an OutboxMessage containing a PdfProcessingCommand.
//   * Both the document and outbox row are saved in a single DB transaction.
// The OutboxPublisher background service later reads pending outbox rows and
// publishes the command to RabbitMQ for asynchronous processing by the Worker.
public class DocumentService
{
    private static readonly Counter UploadCount = Metrics
        .CreateCounter("document_upload_total", "Total number of uploaded documents.");

    private readonly IDocumentRepository _repository;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        IDocumentRepository repository,
        IFileStorage fileStorage,
        ILogger<DocumentService> logger)
    {
        _repository = repository;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    /// <summary>
    /// Core upload logic with Transactional Outbox.
    /// 1. Save file to storage (local volume or MinIO)
    /// 2. Create document record (status=uploaded)
    /// 3. Create outbox row with serialized PdfProcessingCommand
    /// Steps 2+3 happen in the same DB transaction (see DocumentRepository.CreateAsync).
    /// The OutboxPublisher background service will later pick up the outbox row and publish to RabbitMQ.
    /// </summary>
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

        // Atomic insert: document + outbox in one transaction
        await _repository.CreateAsync(document, outboxMessage, cancellationToken);
        UploadCount.Inc();
        _logger.LogInformation("Document {DocumentId} uploaded, filename: {Filename}, outbox message: {OutboxId}", documentId, filename, outboxMessage.Id);

        return new UploadResponse
        {
            DocumentId = documentId,
            Status = "accepted",
            Message = "Document uploaded successfully and queued for processing"
        };
    }

    /// <summary>
    /// Returns all documents ordered by creation date (newest first).
    /// </summary>
    public async Task<List<DocumentListItem>> GetAllDocumentsAsync(CancellationToken cancellationToken = default)
    {
        var documents = await _repository.GetAllAsync(cancellationToken);
        // Ensure deterministic ordering: newest first by CreatedAt, then by Id to break ties.
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

    /// <summary>
    /// Returns extracted text for a document based on its status:
    ///   - Completed → return text with 200
    ///   - Processing/Uploaded → return 202 (retry later)
    ///   - Failed → return 409 (error message included)
    ///   - Not found → return 404
    /// </summary>
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