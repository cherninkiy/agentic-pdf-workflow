namespace Shared.Models;

/// <summary>
/// Defines an agent that can be orchestrated within a workflow.
/// Agent definitions are stored in the database to enable dynamic
/// discovery and configuration of agents at runtime.
///
/// New agents (Translation, NER, Summarization) are added by inserting
/// a new AgentDefinition row and implementing the IAgent interface.
/// </summary>
public class AgentDefinition
{
    /// <summary>
    /// Unique identifier for this agent definition.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name of the agent (e.g., "DocumentProcessing", "Translation").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of what this agent does.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The .NET type name that implements this agent (for dynamic loading).
    /// </summary>
    public string HandlerType { get; set; } = string.Empty;

    /// <summary>
    /// Ordered list of activities this agent performs (JSON array).
    /// Example: ["DownloadDocument","ParseDocument","ExtractText","SaveResult","UpdateStatus"]
    /// </summary>
    public string Activities { get; set; } = "[]";

    /// <summary>
    /// Whether this agent is currently active and can be executed.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this agent definition was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}