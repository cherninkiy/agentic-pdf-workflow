using Microsoft.Extensions.Logging;
using Shared.Interfaces;
using Shared.Models;

namespace Worker.Services;

public class DocumentProcessingService
{
    private readonly IDocumentRepository _repository;
    private readonly IFileStorage _fileStorage;
    private readonly PdfTextExtractor _textExtractor;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(
        IDocumentRepository repository,
        IFileStorage fileStorage,
        PdfTextExtractor textExtractor,
        ILogger<DocumentProcessingService> logger)
    {
        _repository = repository;
        _fileStorage = fileStorage;
        _textExtractor = textExtractor;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single document: download → extract → save.
    /// Implements idempotency (processed_messages table) and optimistic locking
    /// to handle duplicate message delivery and concurrent worker instances.
    ///
    /// Returns true if processing succeeded or was already done (idempotent).
    /// Returns false on failure (sets status='failed' with error message).
    /// </summary>
    public async Task<bool> ProcessDocumentAsync(Guid documentId, Guid messageId, CancellationToken cancellationToken = default)
    {
        // ── Idempotency check ──
        // If this MessageId was already processed, skip (duplicate delivery)
        if (await _repository.IsMessageProcessedAsync(messageId, cancellationToken))
        {
            _logger.LogInformation("Message {MessageId} already processed, skipping", messageId);
            return true;
        }

        // ── Optimistic lock ──
        // Claim the document atomically: only succeeds if status == 'uploaded'
        // This prevents two worker instances from processing the same document
        var claimed = await _repository.TryUpdateStatusAsync(documentId, DocumentStatus.Uploaded, DocumentStatus.Processing, cancellationToken: cancellationToken);
        if (!claimed)
        {
            _logger.LogWarning("Document {DocumentId} not in Uploaded status, skipping (already claimed or not found)", documentId);
            return true;
        }

        try
        {
            var document = await _repository.GetByIdAsync(documentId, cancellationToken);
            if (document == null)
            {
                _logger.LogError("Document {DocumentId} not found after claiming", documentId);
                return false;
            }

            // ── Download PDF from storage ──
            byte[] pdfBytes;
            await using (var stream = await _fileStorage.GetAsync(document.FilePath, cancellationToken))
            {
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, cancellationToken);
                pdfBytes = memoryStream.ToArray();
            }

            // ── Extract text ──
            // PdfPig for text-based PDFs → Azure OCR fallback for scanned documents
            var extractedText = await _textExtractor.ExtractTextAsync(pdfBytes, cancellationToken);

            // ── Save result + mark message processed ──
            // Both operations happen in sequence; MarkMessageProcessed uses a separate
            // insert to the processed_messages table for idempotency tracking
            await _repository.UpdateTextAsync(documentId, extractedText, DocumentStatus.Completed, cancellationToken);
            await _repository.MarkMessageProcessedAsync(messageId, documentId, cancellationToken);

            _logger.LogInformation("Document {DocumentId} processed successfully, text length: {Length}",
                documentId, extractedText?.Length ?? 0);
            return true;
        }
        catch (Exception ex)
        {
            // ── Failure handling ──
            // Set status to 'failed' with error details.
            // MassTransit will retry (5s, 30s, 60s delays), then move to error queue.
            _logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            await _repository.TryUpdateStatusAsync(documentId, DocumentStatus.Processing, DocumentStatus.Failed, errorMessage: ex.Message, cancellationToken: cancellationToken);
            return false;
        }
    }
}