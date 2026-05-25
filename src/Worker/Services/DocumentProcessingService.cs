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

    public async Task<bool> ProcessDocumentAsync(Guid documentId, Guid messageId, CancellationToken cancellationToken = default)
    {
        // Idempotency check
        if (await _repository.IsMessageProcessedAsync(messageId, cancellationToken))
        {
            _logger.LogInformation("Message {MessageId} already processed, skipping", messageId);
            return true;
        }

        // Optimistic lock: try to claim the document (atomic UPDATE WHERE status=uploaded)
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

            // Download PDF
            byte[] pdfBytes;
            await using (var stream = await _fileStorage.GetAsync(document.FilePath, cancellationToken))
            {
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream, cancellationToken);
                pdfBytes = memoryStream.ToArray();
            }

            // Extract text
            var extractedText = await _textExtractor.ExtractTextAsync(pdfBytes, cancellationToken);

            // Save result and mark message processed in a single transaction
            await _repository.UpdateTextAsync(documentId, extractedText, DocumentStatus.Completed, cancellationToken);
            await _repository.MarkMessageProcessedAsync(messageId, documentId, cancellationToken);

            _logger.LogInformation("Document {DocumentId} processed successfully, text length: {Length}", documentId, extractedText?.Length ?? 0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process document {DocumentId}", documentId);
            await _repository.TryUpdateStatusAsync(documentId, DocumentStatus.Processing, DocumentStatus.Failed, errorMessage: ex.Message, cancellationToken: cancellationToken);
            return false;
        }
    }
}