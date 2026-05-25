using Shared.Models;

namespace Shared.Interfaces;

/// <summary>
/// Orchestrates the execution of agents within a workflow.
/// Manages agent discovery, execution, and pipeline composition.
///
/// Example pipeline: DocumentProcessing → Translation → Summarization
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Executes a single agent workflow for the given document.
    /// </summary>
    Task<AgentResult> ExecuteAgentAsync(
        string agentName,
        Guid documentId,
        string filePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a pipeline of agents sequentially.
    /// Output of each agent becomes input to the next.
    /// </summary>
    Task<AgentResult> ExecutePipelineAsync(
        IReadOnlyList<string> agentNames,
        Guid documentId,
        string filePath,
        CancellationToken cancellationToken = default);
}