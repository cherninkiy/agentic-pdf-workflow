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
        // Atomic optimistic lock using raw SQL to avoid race conditions.
        // Uses int status values (matching DocumentStatus enum) — no JOIN needed.
        var fromStatusInt = (int)fromStatus;
        var toStatusInt = (int)toStatus;

        int rows;
        if (toStatus == DocumentStatus.Processing)
        {
            rows = await _context.Database.ExecuteSqlRawAsync(
                "UPDATE documents SET status = {0}, started_at = NOW() WHERE id = {1} AND status = {2}",
                toStatusInt, id, fromStatusInt,
                cancellationToken);
        }
        else
        {
            rows = await _context.Database.ExecuteSqlRawAsync(
                "UPDATE documents SET status = {0}, completed_at = NOW(), error_message = {1} WHERE id = {2} AND status = {3}",
                toStatusInt,
                errorMessage ?? (object)DBNull.Value,
                id, fromStatusInt,
                cancellationToken);
        }

        return rows > 0;
    }

    public async Task UpdateTextAsync(Guid id, string? extractedText, DocumentStatus status, CancellationToken cancellationToken = default)
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE documents SET extracted_text = {0}, status = {1}, completed_at = NOW() WHERE id = {2}",
            extractedText ?? (object)DBNull.Value,
            (int)status,
            id,
            cancellationToken);
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