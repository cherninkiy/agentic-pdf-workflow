using MassTransit;
using Microsoft.Extensions.Logging;
using Shared.Models;
using Worker.Services;

namespace Worker.Consumers;

/// <summary>
/// MassTransit consumer for PdfProcessingCommand messages.
///
/// Processing pipeline:
///   1. Idempotency check — skip if message already processed
///   2. Optimistic lock — claim document via UPDATE WHERE status=uploaded
///   3. Download PDF from shared storage
///   4. Extract text via PdfPig (fallback to Azure OCR if needed)
///   5. Save extracted text + mark message processed (single transaction)
///
/// On failure: throw exception → MassTransit retries with delays (5s, 30s, 60s)
/// After 3 retries: message moves to error queue (DLQ)
/// </summary>
public class PdfProcessingConsumer : IConsumer<PdfProcessingCommand>
{
    private readonly DocumentProcessingService _processingService;
    private readonly ILogger<PdfProcessingConsumer> _logger;

    public PdfProcessingConsumer(DocumentProcessingService processingService, ILogger<PdfProcessingConsumer> logger)
    {
        _processingService = processingService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PdfProcessingCommand> context)
    {
        var command = context.Message;
        _logger.LogInformation("Received processing command for document {DocumentId}, retry: {RetryCount}",
            command.DocumentId, command.RetryCount);

        var success = await _processingService.ProcessDocumentAsync(command.DocumentId, command.MessageId, context.CancellationToken);

        if (!success)
        {
            _logger.LogWarning("Processing failed for document {DocumentId}, sending to error queue", command.DocumentId);
            // Throwing signals MassTransit to apply retry policy, then move to error queue
            throw new Exception($"Processing failed for document {command.DocumentId}");
        }

        _logger.LogInformation("Document {DocumentId} processed successfully, message {MessageId} consumed",
            command.DocumentId, command.MessageId);
    }
}