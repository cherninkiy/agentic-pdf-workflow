using Microsoft.Extensions.Logging;
using Shared.Interfaces;

namespace Worker.Services;

public class PdfTextExtractor
{
    private readonly IOCRService? _ocrService;
    private readonly ILogger<PdfTextExtractor> _logger;

    public PdfTextExtractor(ILogger<PdfTextExtractor> logger, IOCRService? ocrService = null)
    {
        _logger = logger;
        _ocrService = ocrService;
    }

    public async Task<string?> ExtractTextAsync(byte[] pdfContent, CancellationToken cancellationToken = default)
    {
        string? extractedText = null;

        try
        {
            using var pdfDocument = UglyToad.PdfPig.PdfDocument.Open(pdfContent);
            var textParts = new List<string>();

            foreach (var page in pdfDocument.GetPages())
            {
                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                    textParts.Add(pageText.Trim());
            }

            extractedText = textParts.Count > 0 ? string.Join("\n", textParts) : null;
            _logger.LogInformation("PdfPig extracted {PageCount} pages with {Length} chars", pdfDocument.NumberOfPages, extractedText?.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PdfPig extraction failed, will try OCR fallback if available");
        }

        // Fallback to OCR if PdfPig returned empty/null and OCR service is available
        if (string.IsNullOrWhiteSpace(extractedText) && _ocrService != null)
        {
            _logger.LogInformation("PdfPig returned no text, falling back to OCR");
            try
            {
                extractedText = await _ocrService.ExtractTextAsync(pdfContent, cancellationToken);
                _logger.LogInformation("OCR extracted {Length} chars", extractedText?.Length ?? 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR extraction failed");
                throw;
            }
        }

        return extractedText;
    }
}