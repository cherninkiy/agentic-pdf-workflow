using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;
using Shared.Models;

namespace ApiGateway.Data;

public class DocumentRepository : IDocumentRepository
{
    private readonly AppDbContext _context;

    public DocumentRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task CreateAsync(DocumentDto document, OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
    {
        await _context.Documents.AddAsync(document, cancellationToken);
        await _context.OutboxMessages.AddAsync(outboxMessage, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<List<DocumentDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Documents
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryUpdateStatusAsync(Guid id, DocumentStatus fromStatus, DocumentStatus toStatus, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var document = await _context.Documents.FirstOrDefaultAsync(d => d.Id == id && d.Status == fromStatus, cancellationToken);
        if (document == null) return false;

        document.Status = toStatus;
        if (toStatus == DocumentStatus.Processing)
            document.StartedAt = DateTime.UtcNow;
        else if (toStatus is DocumentStatus.Completed or DocumentStatus.Failed)
            document.CompletedAt = DateTime.UtcNow;

        if (errorMessage != null)
            document.ErrorMessage = errorMessage;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task UpdateTextAsync(Guid id, string? extractedText, DocumentStatus status, CancellationToken cancellationToken = default)
    {
        var document = await _context.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (document == null) return;

        document.ExtractedText = extractedText;
        document.Status = status;
        document.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<OutboxMessage>> GetOutboxPendingAsync(CancellationToken cancellationToken = default)
    {
        return await _context.OutboxMessages
            .Where(o => o.ProcessedAt == null)
            .OrderBy(o => o.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkOutboxProcessedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var message = await _context.OutboxMessages.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (message != null)
        {
            message.ProcessedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkMessageProcessedAsync(Guid messageId, Guid documentId, CancellationToken cancellationToken = default)
    {
        _context.ProcessedMessages.Add(new ProcessedMessage
        {
            MessageId = messageId,
            DocumentId = documentId,
            ProcessedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _context.ProcessedMessages.AnyAsync(p => p.MessageId == messageId, cancellationToken);
    }
}