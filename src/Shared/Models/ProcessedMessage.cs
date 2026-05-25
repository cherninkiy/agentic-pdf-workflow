namespace Shared.Models;

public class ProcessedMessage
{
    public Guid MessageId { get; set; }
    public Guid DocumentId { get; set; }
    public DateTime ProcessedAt { get; set; }
}