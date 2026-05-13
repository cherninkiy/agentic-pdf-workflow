namespace Shared.Models;

/// <summary>
/// Represents a checkpoint in an agent workflow.
/// Checkpoints enable durable execution — if a worker crashes mid-processing,
/// the agent can resume from the last saved checkpoint instead of starting over.
///
/// Stored in PostgreSQL via EF Core (Worker project owns the DbContext).
/// </summary>
public class WorkflowCheckpoint
{
    /// <summary>
    /// Unique identifier for this checkpoint record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The agent that owns this checkpoint (e.g., "DocumentProcessing").
    /// </summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// The document being processed — links to the documents table.
    /// </summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// The current step in the workflow (e.g., "DownloadDocument", "ParseDocument").
    /// </summary>
    public string CurrentActivity { get; set; } = string.Empty;

    /// <summary>
    /// Serialized state data for the current step (JSON).
    /// Contains step-specific data needed to resume execution.
    /// </summary>
    public string? StateData { get; set; }

    /// <summary>
    /// Whether this checkpoint represents a completed workflow.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// Whether this checkpoint represents a failed workflow.
    /// </summary>
    public bool IsFailed { get; set; }

    /// <summary>
    /// Error message if the workflow failed at this checkpoint.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When this checkpoint was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this checkpoint was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}