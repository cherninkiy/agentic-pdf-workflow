using Microsoft.Extensions.Logging;
using Shared.Interfaces;
using Shared.Models;
using Worker.Services;

namespace Worker.Agents;

/// <summary>
/// MAF agent that orchestrates the PDF document processing workflow.
///
/// Workflow activities (executed in order):
///   1. DownloadDocument  — download PDF from file storage
///   2. ParseDocument     — extract text via PdfPig
///   3. ExtractText       — OCR fallback via Tesseract if PdfPig returns empty
///   4. SaveResult        — save extracted text to database
///   5. UpdateStatus      — mark document as completed
///
/// Each activity saves a checkpoint after execution. If the worker crashes,
/// the agent resumes from the last completed activity instead of starting over.
///
/// Reuses existing services: PdfTextExtractor, TesseractOcrService, IDocumentRepository.
/// </summary>
public class DocumentProcessingAgent : IAgent
{
    public string AgentName => "DocumentProcessing";

    public IReadOnlyList<string> Activities => new List<string>
    {
        "DownloadDocument",
        "ParseDocument",
        "ExtractText",
        "SaveResult",
        "UpdateStatus"
    }.AsReadOnly();

    private readonly PdfTextExtractor _textExtractor;
    private readonly IDocumentRepository _repository;
    private readonly IFileStorage _fileStorage;
    private readonly ILogger<DocumentProcessingAgent> _logger;

    public DocumentProcessingAgent(
        PdfTextExtractor textExtractor,
        IDocumentRepository repository,
        IFileStorage fileStorage,
        ILogger<DocumentProcessingAgent> logger)
    {
        _textExtractor = textExtractor;
        _repository = repository;
        _fileStorage = fileStorage;
        _logger = logger;
    }

    /// <summary>
    /// Executes the full document processing workflow with checkpoint support.
    /// Skips already-completed activities (resume after crash).
    /// </summary>
    public async Task<AgentResult> ExecuteAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting {Agent} workflow for document {DocumentId}",
            AgentName, context.DocumentId);

        // Load completed checkpoints to determine which activities to skip
        var completedCheckpoints = await checkpointStore.LoadCompletedCheckpointsAsync(
            AgentName, context.DocumentId, cancellationToken);
        var completedActivities = completedCheckpoints
            .Where(c => c.IsCompleted && !c.IsFailed)
            .Select(c => c.CurrentActivity)
            .ToHashSet();

        try
        {
            // ── Activity 1: DownloadDocument ──
            byte[] pdfBytes;
            if (completedActivities.Contains("DownloadDocument"))
            {
                _logger.LogInformation("Skipping DownloadDocument (already completed)");
                // Restore PDF bytes from checkpoint state
                var checkpoint = completedCheckpoints.First(c => c.CurrentActivity == "DownloadDocument");
                pdfBytes = Convert.FromBase64String(checkpoint.StateData ?? string.Empty);
            }
            else
            {
                pdfBytes = await ExecuteDownloadDocumentAsync(context, checkpointStore, cancellationToken);
            }

            // ── Activity 2: ParseDocument ──
            string? parsedText;
            if (completedActivities.Contains("ParseDocument"))
            {
                _logger.LogInformation("Skipping ParseDocument (already completed)");
                var checkpoint = completedCheckpoints.First(c => c.CurrentActivity == "ParseDocument");
                parsedText = checkpoint.StateData;
            }
            else
            {
                parsedText = await ExecuteParseDocumentAsync(pdfBytes, context, checkpointStore, cancellationToken);
            }

            // ── Activity 3: ExtractText (OCR fallback) ──
            string? extractedText;
            if (completedActivities.Contains("ExtractText"))
            {
                _logger.LogInformation("Skipping ExtractText (already completed)");
                var checkpoint = completedCheckpoints.First(c => c.CurrentActivity == "ExtractText");
                extractedText = checkpoint.StateData;
            }
            else
            {
                extractedText = await ExecuteExtractTextAsync(parsedText, pdfBytes, context, checkpointStore, cancellationToken);
            }

            // ── Activity 4: SaveResult ──
            if (!completedActivities.Contains("SaveResult"))
            {
                await ExecuteSaveResultAsync(extractedText, context, checkpointStore, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Skipping SaveResult (already completed)");
            }

            // ── Activity 5: UpdateStatus ──
            if (!completedActivities.Contains("UpdateStatus"))
            {
                await ExecuteUpdateStatusAsync(context, checkpointStore, cancellationToken);
            }
            else
            {
                _logger.LogInformation("Skipping UpdateStatus (already completed)");
            }

            // Clean up checkpoints after successful completion
            await checkpointStore.DeleteCheckpointsAsync(AgentName, context.DocumentId, cancellationToken);

            _logger.LogInformation("Document {DocumentId} processed successfully", context.DocumentId);
            return AgentResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document processing failed for {DocumentId}", context.DocumentId);
            // Save failure checkpoint
            await checkpointStore.SaveCheckpointAsync(
                AgentName, context.DocumentId, "Failure",
                AgentResult.Failure(ex.Message), cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Activity 1: Downloads the PDF file from storage.
    /// Stores PDF bytes as Base64 in checkpoint for resume support.
    /// </summary>
    private async Task<byte[]> ExecuteDownloadDocumentAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Activity: DownloadDocument for {DocumentId}", context.DocumentId);

        await using var stream = await _fileStorage.GetAsync(context.FilePath, cancellationToken);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        var pdfBytes = memoryStream.ToArray();

        // Save checkpoint with PDF bytes as Base64 (for resume)
        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "DownloadDocument",
            AgentResult.Success(Convert.ToBase64String(pdfBytes)), cancellationToken);

        _logger.LogInformation("Downloaded {Bytes} bytes for {DocumentId}", pdfBytes.Length, context.DocumentId);
        return pdfBytes;
    }

    /// <summary>
    /// Activity 2: Parses PDF text using PdfPig.
    /// Returns extracted text or null if PDF is scanned (needs OCR).
    /// </summary>
    private async Task<string?> ExecuteParseDocumentAsync(
        byte[] pdfBytes,
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Activity: ParseDocument for {DocumentId}", context.DocumentId);

        // PdfTextExtractor handles PdfPig internally
        var text = await _textExtractor.ExtractTextAsync(pdfBytes, cancellationToken);

        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "ParseDocument",
            AgentResult.Success(text), cancellationToken);

        _logger.LogInformation("ParseDocument extracted {Length} chars for {DocumentId}",
            text?.Length ?? 0, context.DocumentId);
        return text;
    }

    /// <summary>
    /// Activity 3: OCR fallback — if PdfPig returned empty, use Tesseract.
    /// PdfTextExtractor already handles the fallback internally.
    /// </summary>
    private async Task<string?> ExecuteExtractTextAsync(
        string? parsedText,
        byte[] pdfBytes,
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Activity: ExtractText for {DocumentId}", context.DocumentId);

        // If ParseDocument already got text, no need for OCR
        var finalText = !string.IsNullOrWhiteSpace(parsedText) ? parsedText : null;

        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "ExtractText",
            AgentResult.Success(finalText), cancellationToken);

        _logger.LogInformation("ExtractText result: {Length} chars for {DocumentId}",
            finalText?.Length ?? 0, context.DocumentId);
        return finalText;
    }

    /// <summary>
    /// Activity 4: Saves extracted text to the database.
    /// </summary>
    private async Task ExecuteSaveResultAsync(
        string? extractedText,
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Activity: SaveResult for {DocumentId}", context.DocumentId);

        await _repository.UpdateTextAsync(
            context.DocumentId, extractedText, DocumentStatus.Processing, cancellationToken);

        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "SaveResult",
            AgentResult.Success(), cancellationToken);

        _logger.LogInformation("Saved text for {DocumentId}", context.DocumentId);
    }

    /// <summary>
    /// Activity 5: Updates document status to Completed.
    /// </summary>
    private async Task ExecuteUpdateStatusAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Activity: UpdateStatus for {DocumentId}", context.DocumentId);

        await _repository.TryUpdateStatusAsync(
            context.DocumentId, DocumentStatus.Processing, DocumentStatus.Completed,
            cancellationToken: cancellationToken);

        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "UpdateStatus",
            AgentResult.Success(), cancellationToken);

        _logger.LogInformation("Document {DocumentId} marked as Completed", context.DocumentId);
    }
}