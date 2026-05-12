using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;
using Shared.Models;

namespace Worker.Data;

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
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<List<DocumentDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Documents.OrderByDescending(d => d.CreatedAt).ToListAsync(cancellationToken);
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
                "UPDATE documents SET status = {0}, started_at = {1} WHERE id = {2} AND status = {3}",
                new object[] { toStatusInt, DateTime.UtcNow, id, fromStatusInt },
                cancellationToken);
        }
        else
        {
            rows = await _context.Database.ExecuteSqlRawAsync(
                "UPDATE documents SET status = {0}, completed_at = {1}, error_message = {2} WHERE id = {3} AND status = {4}",
                new object[] {
                    toStatusInt, DateTime.UtcNow,
                    errorMessage ?? (object)DBNull.Value,
                    id, fromStatusInt
                },
                cancellationToken);
        }

        return rows > 0;
    }

    public async Task UpdateTextAsync(Guid id, string? extractedText, DocumentStatus status, CancellationToken cancellationToken = default)
    {
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE documents SET extracted_text = {0}, status = {1}, completed_at = {2} WHERE id = {3}",
            new object[] { extractedText ?? (object)DBNull.Value, (int)status, DateTime.UtcNow, id },
            cancellationToken);
    }

    public async Task<List<OutboxMessage>> GetOutboxPendingAsync(CancellationToken cancellationToken = default)
    {
        return await Task.FromResult(new List<OutboxMessage>());
    }

    public Task MarkOutboxProcessedAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

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