using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Interfaces;
using Shared.Models;

namespace Worker.Data;

/// <summary>
/// PostgreSQL-backed implementation of ICheckpointStore.
/// Stores workflow checkpoints in the workflow_checkpoints table.
///
/// Checkpoints enable durable execution: if a worker crashes mid-processing,
/// the agent resumes from the last saved checkpoint instead of starting over.
/// </summary>
public class PostgreSqlCheckpointStore : ICheckpointStore
{
    private readonly AppDbContext _context;
    private readonly ILogger<PostgreSqlCheckpointStore> _logger;

    public PostgreSqlCheckpointStore(AppDbContext context, ILogger<PostgreSqlCheckpointStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Saves a checkpoint for the given agent, document, and activity.
    /// Uses upsert semantics: inserts new or updates existing checkpoint.
    /// </summary>
    public async Task SaveCheckpointAsync(
        string agentName,
        Guid documentId,
        string activityName,
        AgentResult result,
        CancellationToken cancellationToken = default)
    {
        var existing = await _context.WorkflowCheckpoints
            .FirstOrDefaultAsync(c => c.AgentName == agentName
                                      && c.DocumentId == documentId
                                      && c.CurrentActivity == activityName,
                cancellationToken);

        var errorMessage = result.ErrorMessage?.Length > 4096
            ? result.ErrorMessage[..4096]
            : result.ErrorMessage;

        if (existing != null)
        {
            // Update existing checkpoint
            existing.StateData = result.OutputData;
            existing.IsCompleted = result.IsSuccess;
            existing.IsFailed = !result.IsSuccess;
            existing.ErrorMessage = errorMessage;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Create new checkpoint
            _context.WorkflowCheckpoints.Add(new WorkflowCheckpoint
            {
                Id = Guid.NewGuid(),
                AgentName = agentName,
                DocumentId = documentId,
                CurrentActivity = activityName,
                StateData = result.OutputData,
                IsCompleted = result.IsSuccess,
                IsFailed = !result.IsSuccess,
                ErrorMessage = errorMessage,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogDebug("Checkpoint saved: {AgentName}/{DocumentId}/{Activity}",
            agentName, documentId, activityName);
    }

    /// <summary>
    /// Loads the most recent checkpoint for the given agent and document.
    /// Returns null if no checkpoint exists (first run).
    /// </summary>
    public async Task<WorkflowCheckpoint?> LoadCheckpointAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        return await _context.WorkflowCheckpoints
            .Where(c => c.AgentName == agentName && c.DocumentId == documentId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Loads all completed checkpoints for the given agent and document.
    /// Used to determine which activities have already been executed (for resume).
    /// </summary>
    public async Task<IReadOnlyList<WorkflowCheckpoint>> LoadCompletedCheckpointsAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var checkpoints = await _context.WorkflowCheckpoints
            .Where(c => c.AgentName == agentName
                        && c.DocumentId == documentId
                        && c.IsCompleted && !c.IsFailed)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return checkpoints.AsReadOnly();
    }

    /// <summary>
    /// Deletes all checkpoints for the given agent and document.
    /// Called after workflow completion (success or failure) to clean up.
    /// </summary>
    public async Task DeleteCheckpointsAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        var checkpoints = await _context.WorkflowCheckpoints
            .Where(c => c.AgentName == agentName && c.DocumentId == documentId)
            .ToListAsync(cancellationToken);

        if (checkpoints.Count > 0)
        {
            _context.WorkflowCheckpoints.RemoveRange(checkpoints);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Checkpoints deleted: {AgentName}/{DocumentId} ({Count} records)",
                agentName, documentId, checkpoints.Count);
        }
    }
}