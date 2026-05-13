namespace Shared.Models;

/// <summary>
/// Context passed to each agent activity during execution.
/// Contains the document being processed, checkpoint state, and
/// results from previous activities in the workflow.
/// </summary>
public class AgentContext
{
    /// <summary>
    /// The document being processed.
    /// </summary>
    public Guid DocumentId { get; set; }

    /// <summary>
    /// The file path of the document in storage.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// The name of the agent executing this workflow.
    /// </summary>
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// The current activity being executed.
    /// </summary>
    public string CurrentActivity { get; set; } = string.Empty;

    /// <summary>
    /// Results from previously completed activities.
    /// Key = activity name, Value = serialized result data.
    /// </summary>
    public Dictionary<string, string> PreviousResults { get; set; } = new();

    /// <summary>
    /// Gets the result of a previous activity by name.
    /// </summary>
    public T? GetPreviousResult<T>(string activityName)
    {
        if (PreviousResults.TryGetValue(activityName, out var data) && data != null)
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(data);
        }
        return default;
    }

    /// <summary>
    /// Stores the result of the current activity for downstream activities.
    /// </summary>
    public void SetResult<T>(string activityName, T result)
    {
        PreviousResults[activityName] = System.Text.Json.JsonSerializer.Serialize(result);
    }
}