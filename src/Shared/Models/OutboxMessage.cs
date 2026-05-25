namespace Shared.Models;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string MessagePayload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
}