using Shared.Models;

namespace Shared.Interfaces;

/// <summary>
/// Persistence layer for workflow checkpoints.
/// Implementations can use PostgreSQL, Redis, or in-memory storage.
///
/// Checkpoints enable durable execution: if a worker crashes mid-processing,
/// the agent resumes from the last saved checkpoint instead of starting over.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>
    /// Saves a checkpoint for the given agent and document.
    /// Overwrites any existing checkpoint for the same agent+document+activity.
    /// </summary>
    Task SaveCheckpointAsync(
        string agentName,
        Guid documentId,
        string activityName,
        AgentResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the most recent checkpoint for the given agent and document.
    /// Returns null if no checkpoint exists.
    /// </summary>
    Task<WorkflowCheckpoint?> LoadCheckpointAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all completed checkpoints for the given agent and document.
    /// Used to determine which activities have already been executed.
    /// </summary>
    Task<IReadOnlyList<WorkflowCheckpoint>> LoadCompletedCheckpointsAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all checkpoints for the given agent and document.
    /// Called after workflow completion (success or failure).
    /// </summary>
    Task DeleteCheckpointsAsync(
        string agentName,
        Guid documentId,
        CancellationToken cancellationToken = default);
}