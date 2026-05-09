namespace Shared.Models;

public class DocumentDto
{
    public Guid Id { get; set; }
    public string Filename { get; set; } = string.Empty;
    public DocumentStatus Status { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ExtractedText { get; set; }
}