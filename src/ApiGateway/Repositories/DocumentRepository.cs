using ApiGateway.Data;
using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;
using Shared.Models;
using Shared.Repositories;

namespace ApiGateway.Repositories;

/// <summary>
/// ApiGateway-specific document repository.
/// Inherits shared SQL operations from DocumentRepositoryBase.
/// Implements both IDocumentRepository and IOutboxRepository.
/// </summary>
public class DocumentRepository : DocumentRepositoryBase<GatewayDbContext>, IDocumentRepository, IOutboxRepository
{
    public DocumentRepository(GatewayDbContext context) : base(context) { }

    public async Task CreateAsync(DocumentDto document, OutboxMessage outboxMessage, CancellationToken ct = default)
    {
        await Context.Documents.AddAsync(document, ct);
        await Context.OutboxMessages.AddAsync(outboxMessage, ct);
        await Context.SaveChangesAsync(ct);
    }

    public async Task<List<OutboxMessage>> GetOutboxPendingAsync(CancellationToken ct = default)
    {
        return await Context.OutboxMessages
            .Where(o => o.ProcessedAt == null)
            .OrderBy(o => o.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task MarkOutboxProcessedAsync(Guid id, CancellationToken ct = default)
    {
        var message = await Context.OutboxMessages.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (message != null)
        {
            message.ProcessedAt = DateTime.UtcNow;
            await Context.SaveChangesAsync(ct);
        }
    }
}