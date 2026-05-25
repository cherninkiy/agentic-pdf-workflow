using MassTransit;

namespace Shared.Models;

public class PdfProcessingCommand : CorrelatedBy<Guid>
{
    public Guid DocumentId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public Guid MessageId { get; set; }
    public int RetryCount { get; set; }

    public Guid CorrelationId => DocumentId;
}