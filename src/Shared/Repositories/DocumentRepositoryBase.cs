using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Shared.Repositories;

public abstract class DocumentRepositoryBase<TContext> where TContext : DbContext
{
    protected readonly TContext Context;

    protected DocumentRepositoryBase(TContext context)
    {
        Context = context;
    }

    public virtual async Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await Context.Set<DocumentDto>().FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public virtual async Task AddAsync(DocumentDto document, CancellationToken ct = default)
    {
        await Context.Set<DocumentDto>().AddAsync(document, ct);
        await Context.SaveChangesAsync(ct);
    }

    public virtual async Task<List<DocumentDto>> GetAllAsync(CancellationToken ct = default)
    {
        return await Context.Set<DocumentDto>().OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
    }

    public virtual async Task<bool> TryUpdateStatusAsync(
        Guid id, DocumentStatus fromStatus, DocumentStatus toStatus,
        string? errorMessage = null, CancellationToken ct = default)
    {
        var fromStatusInt = (int)fromStatus;
        var toStatusInt = (int)toStatus;

        int rows;
        if (toStatus == DocumentStatus.Processing)
        {
            rows = await Context.Database.ExecuteSqlRawAsync(
                "UPDATE documents SET status = {0}, started_at = {1} WHERE id = {2} AND status = {3}",
                toStatusInt, DateTime.UtcNow, id, fromStatusInt, ct);
        }
        else
        {
            rows = await Context.Database.ExecuteSqlRawAsync(
                "UPDATE documents SET status = {0}, completed_at = {1}, error_message = {2} WHERE id = {3} AND status = {4}",
                toStatusInt, DateTime.UtcNow, errorMessage!, id, fromStatusInt, ct);
        }

        return rows > 0;
    }

    public virtual async Task UpdateTextAsync(Guid id, string? extractedText, DocumentStatus status, CancellationToken ct = default)
    {
        await Context.Database.ExecuteSqlRawAsync(
            "UPDATE documents SET extracted_text = {0}, status = {1}, completed_at = {2} WHERE id = {3}",
            extractedText!, (int)status, DateTime.UtcNow, id, ct);
    }

    public virtual async Task MarkMessageProcessedAsync(Guid messageId, Guid documentId, CancellationToken ct = default)
    {
        Context.Set<ProcessedMessage>().Add(new ProcessedMessage
        {
            MessageId = messageId,
            DocumentId = documentId,
            ProcessedAt = DateTime.UtcNow
        });
        await Context.SaveChangesAsync(ct);
    }

    public virtual async Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken ct = default)
    {
        return await Context.Set<ProcessedMessage>().AnyAsync(p => p.MessageId == messageId, ct);
    }
}