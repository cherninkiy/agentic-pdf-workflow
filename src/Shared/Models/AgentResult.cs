namespace Shared.Models;

/// <summary>
/// Represents the result of an agent activity execution.
/// Used to communicate success/failure and output data between workflow steps.
/// </summary>
public class AgentResult
{
    /// <summary>
    /// Whether the activity completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// Output data from the activity (serialized as JSON for checkpoint storage).
    /// </summary>
    public string? OutputData { get; set; }

    /// <summary>
    /// Error message if the activity failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Creates a successful result with optional output data.
    /// </summary>
    public static AgentResult Success(string? outputData = null)
    {
        return new AgentResult { IsSuccess = true, OutputData = outputData };
    }

    /// <summary>
    /// Creates a failed result with an error message.
    /// </summary>
    public static AgentResult Failure(string errorMessage)
    {
        return new AgentResult { IsSuccess = false, ErrorMessage = errorMessage };
    }
}