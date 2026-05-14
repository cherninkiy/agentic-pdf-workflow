using MassTransit;
using Microsoft.Extensions.Logging;
using Shared.Exceptions;
using Shared.Interfaces;
using Shared.Models;
using Worker.Agents;

namespace Worker.Consumers;

/// <summary>
/// MassTransit consumer for PdfProcessingCommand messages.
///
/// This consumer acts as the entry point between the message broker (RabbitMQ)
/// and the MAF agent workflow. MassTransit handles:
///   - Message delivery and retry (5s → 30s → 60s delays)
///   - Dead letter queue after 3 failed retries
///   - Idempotent message handling
///
/// The actual document processing is delegated to DocumentProcessingAgent,
/// which orchestrates the workflow with checkpoint-based durability:
///   1. DownloadDocument  — download PDF from storage
///   2. ParseDocument     — extract text via PdfPig
///   3. ExtractText       — OCR fallback via Tesseract
///   4. SaveResult        — save text to database
///   5. UpdateStatus      — mark document as completed
///
/// If the worker crashes mid-processing, the agent resumes from the last
/// checkpoint instead of starting over.
/// </summary>
public class PdfProcessingConsumer : IConsumer<PdfProcessingCommand>
{
    private readonly DocumentProcessingAgent _agent;
    private readonly ICheckpointStore _checkpointStore;
    private readonly IDocumentRepository _repository;
    private readonly ILogger<PdfProcessingConsumer> _logger;

    public PdfProcessingConsumer(
        DocumentProcessingAgent agent,
        ICheckpointStore checkpointStore,
        IDocumentRepository repository,
        ILogger<PdfProcessingConsumer> logger)
    {
        _agent = agent;
        _checkpointStore = checkpointStore;
        _repository = repository;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PdfProcessingCommand> context)
    {
        var command = context.Message;
        _logger.LogInformation("Received processing command for document {DocumentId}, retry: {RetryCount}",
            command.DocumentId, command.RetryCount);

        // ── Idempotency check (at message level) ──
        // The agent has its own checkpoint-based idempotency,
        // but this check prevents unnecessary processing of duplicate messages.
        if (await _repository.IsMessageProcessedAsync(command.MessageId, context.CancellationToken))
        {
            _logger.LogInformation("Message {MessageId} already processed, skipping", command.MessageId);
            return;
        }

        // ── Build agent context ──
        var agentContext = new AgentContext
        {
            DocumentId = command.DocumentId,
            FilePath = command.FilePath,
            AgentName = _agent.AgentName,
            CurrentActivity = _agent.Activities.First()
        };

        try
        {
            // ── Execute the MAF agent workflow ──
            var result = await _agent.ExecuteAsync(agentContext, _checkpointStore, context.CancellationToken);

            if (!result.IsSuccess)
            {
                _logger.LogWarning("Agent workflow failed for document {DocumentId}: {Error}",
                    command.DocumentId, result.ErrorMessage);
                throw new DocumentProcessingException(command.DocumentId, result.ErrorMessage!);
            }

            // ── Mark message as processed (idempotency) ──
            await _repository.MarkMessageProcessedAsync(command.MessageId, command.DocumentId, context.CancellationToken);

            _logger.LogInformation("Document {DocumentId} processed successfully, message {MessageId} consumed",
                command.DocumentId, command.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing failed for document {DocumentId}", command.DocumentId);

            // Update document status to Failed
            await _repository.TryUpdateStatusAsync(
                command.DocumentId, DocumentStatus.Processing, DocumentStatus.Failed,
                errorMessage: ex.Message, cancellationToken: context.CancellationToken);

            // Throw to trigger MassTransit retry → DLQ after 3 attempts
            throw;
        }
    }
}