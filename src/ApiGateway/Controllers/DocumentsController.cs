using Microsoft.AspNetCore.Mvc;
using Shared.Models;
using ApiGateway.Services;

namespace ApiGateway.Controllers;

/// <summary>
/// REST API for PDF document management.
/// Implements the document status lifecycle: uploaded → processing → completed | failed.
///
/// Endpoints:
///   POST /upload — Upload a PDF file (max 4 MB). Returns 202 Accepted with DocumentId.
///   GET  /list   — List all documents with metadata (id, filename, status, created_at).
///   GET  /text/{id} — Get extracted text. Returns 200 (ready), 202 (processing), 409 (failed).
/// </summary>
[ApiController]
[Route("")]
public class DocumentsController : ControllerBase
{
    private readonly DocumentService _documentService;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(DocumentService documentService, ILogger<DocumentsController> logger)
    {
        _documentService = documentService;
        _logger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(4 * 1024 * 1024)] // 4 MB limit (Azure F0 tier)
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new UploadResponse { Status = "error", Message = "No file provided" });

        if (file.Length > 4 * 1024 * 1024)
            return BadRequest(new UploadResponse { Status = "error", Message = "File size exceeds 4 MB limit" });

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != ".pdf")
            return BadRequest(new UploadResponse { Status = "error", Message = "Only PDF files are accepted" });

        await using var stream = file.OpenReadStream();
        var result = await _documentService.CreateDocumentAsync(stream, file.FileName, cancellationToken);

        return Accepted(result);
    }

    [HttpGet("list")]
    public async Task<IActionResult> GetList(CancellationToken cancellationToken)
    {
        var documents = await _documentService.GetAllDocumentsAsync(cancellationToken);
        return Ok(documents);
    }

    [HttpGet("text/{id:guid}")]
    public async Task<IActionResult> GetText(Guid id, CancellationToken cancellationToken)
    {
        var (document, errorMessage, statusCode) = await _documentService.GetDocumentTextAsync(id, cancellationToken);

        if (statusCode == 404)
            return NotFound(new { error = errorMessage });

        if (statusCode == 409)
            return Conflict(new { error = errorMessage, documentId = id, status = "failed" });

        if (statusCode == 202)
            return Accepted(new { documentId = id, status = document!.Status.ToString().ToLower(), message = errorMessage });

        return Ok(new { documentId = id, status = "completed", extractedText = document!.ExtractedText });
    }
}