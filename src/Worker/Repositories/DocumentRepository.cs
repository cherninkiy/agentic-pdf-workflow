using Microsoft.EntityFrameworkCore;
using Shared.Interfaces;
using Shared.Models;
using Shared.Repositories;
using Worker.Data;

namespace Worker.Repositories;

/// <summary>
/// Worker-specific document repository.
/// Inherits shared SQL operations from DocumentRepositoryBase.
/// Worker does not use Outbox — it consumes messages from RabbitMQ.
/// </summary>
public class DocumentRepository : DocumentRepositoryBase<WorkerDbContext>, IDocumentRepository
{
    public DocumentRepository(WorkerDbContext context) : base(context) { }

    public async Task<List<WorkflowCheckpoint>> GetCompletedCheckpointsAsync(
        string agentName, Guid documentId, CancellationToken ct = default)
    {
        return await Context.WorkflowCheckpoints
            .Where(c => c.AgentName == agentName && c.DocumentId == documentId && c.IsCompleted && !c.IsFailed)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<WorkflowCheckpoint?> GetLastCheckpointAsync(
        string agentName, Guid documentId, CancellationToken ct = default)
    {
        return await Context.WorkflowCheckpoints
            .Where(c => c.AgentName == agentName && c.DocumentId == documentId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }
}