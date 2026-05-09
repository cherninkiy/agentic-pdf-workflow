namespace Shared.Models;

public class DocumentListItem
{
    public Guid Id { get; set; }
    public string Filename { get; set; } = string.Empty;
    public DocumentStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}