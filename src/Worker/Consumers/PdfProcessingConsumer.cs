using MassTransit;
using Microsoft.Extensions.Logging;
using Shared.Models;
using Worker.Services;

namespace Worker.Consumers;

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
            throw new Exception($"Processing failed for document {command.DocumentId}");
        }

        _logger.LogInformation("Document {DocumentId} processed successfully, message {MessageId} consumed",
            command.DocumentId, command.MessageId);
    }
}