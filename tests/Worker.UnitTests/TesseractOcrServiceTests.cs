using System.Text;
using Microsoft.Extensions.Logging;
using Moq;
using Worker.Services;

namespace Worker.UnitTests;

/// <summary>
/// Tests for TesseractOcrService that exercise the full OCR pipeline:
/// pdftoppm (PDF -> PNG) -> Tesseract OCR -> text output.
/// Requires tesseract-ocr and poppler-utils installed on the host.
/// </summary>
public class TesseractOcrServiceTests : IDisposable
{
    private readonly TesseractOcrService _service;
    private readonly string _tempDir;

    public TesseractOcrServiceTests()
    {
        var loggerMock = new Mock<ILogger<TesseractOcrService>>();
        _service = new TesseractOcrService(loggerMock.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"tesseract_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsText_ForTextedPdf()
    {
        // Arrange
        var pdfPath = ResolveSamplePath("true-pdf-sample-1.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        // Act
        var result = await _service.ExtractTextAsync(pdfBytes);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        // It's a real PDF with text content, so OCR should produce meaningful output
        Assert.True(result.Length > 50, $"OCR result too short: '{result?.Substring(0, Math.Min(100, result?.Length ?? 0))}'");
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsNull_ForEmptyPdf()
    {
        // Arrange - minimal valid PDF with no text content
        var emptyPdf = CreateEmptyPdf();

        // Act
        var result = await _service.ExtractTextAsync(emptyPdf);

        // Assert - blank page produces no text
        Assert.Null(result);
    }

    [Fact]
    public async Task ExtractTextAsync_Throws_OnCancellation()
    {
        // Arrange
        var pdfPath = ResolveSamplePath("true-pdf-sample-1.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        // TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.ExtractTextAsync(pdfBytes, cts.Token));
    }

    [Fact]
    public async Task ExtractTextAsync_ReturnsText_ForScannedPdf()
    {
        // Arrange
        var pdfPath = ResolveSamplePath("Non-text-searchable.pdf");
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);

        // Act
        var result = await _service.ExtractTextAsync(pdfBytes);

        // Assert - should OCR some text from scanned document
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.True(result.Length > 10, $"OCR result too short: '{result?.Substring(0, Math.Min(50, result?.Length ?? 0))}'");
    }

    private static string ResolveSamplePath(string filename)
    {
        var baseDir = AppContext.BaseDirectory;
        // Walk up from bin/Debug/net8.0 to solution root, then into samples/
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples")))
            dir = dir.Parent;

        var path = Path.Combine(dir?.FullName ?? ".", "samples", filename);
        Assert.True(File.Exists(path), $"Sample PDF not found at {path}");
        return path;
    }

    private static byte[] CreateEmptyPdf()
    {
        var content = @"%PDF-1.4
1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj
2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj
3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj
xref
0 4
0000000000 65535 f 
0000000009 00000 n 
0000000058 00000 n 
0000000115 00000 n 
trailer<</Size 4/Root 1 0 R>>
startxref
190
%%EOF";
        return Encoding.ASCII.GetBytes(content);
    }
}