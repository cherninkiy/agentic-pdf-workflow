using Shared.Models;

namespace Shared.Interfaces;

/// <summary>
/// Defines an agent that can execute a workflow of activities.
/// Each agent has a name, a list of ordered activities, and executes
/// them sequentially with checkpoint support.
///
/// To create a new agent (e.g., TranslationAgent):
/// 1. Implement this interface
/// 2. Define the ordered list of activities
/// 3. Execute each activity, saving checkpoints between steps
/// 4. Register the agent in DI and add a row to agent_definitions table
/// </summary>
public interface IAgent
{
    /// <summary>
    /// Unique name of the agent (e.g., "DocumentProcessing", "Translation").
    /// </summary>
    string AgentName { get; }

    /// <summary>
    /// Ordered list of activity names this agent performs.
    /// </summary>
    IReadOnlyList<string> Activities { get; }

    /// <summary>
    /// Executes the full workflow for the given context.
    /// Activities are executed in order, with checkpoints saved after each.
    /// If a checkpoint exists for a completed activity, it is skipped (resume).
    /// </summary>
    /// <param name="context">The agent context with document info and state.</param>
    /// <param name="checkpointStore">Store for saving/loading checkpoints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The final result of the workflow.</returns>
    Task<AgentResult> ExecuteAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken = default);
}