namespace Shared.Interfaces;

public interface IOCRService
{
    Task<string?> ExtractTextAsync(byte[] pdfContent, CancellationToken cancellationToken = default);
}