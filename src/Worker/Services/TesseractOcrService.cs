using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shared.Interfaces;

namespace Worker.Services;

/// <summary>
/// OCR service using local Tesseract OCR via system process.
/// Converts PDF pages to images via pdftoppm, then runs Tesseract.
///
/// Process:
///   1. Convert PDF page to PNG using pdftoppm (poppler-utils)
///   2. Run Tesseract on the image to extract text
///   3. Combine all pages
///
/// Dependencies (installed on host):
///   - tesseract-ocr (with language data, e.g. eng, rus)
///   - poppler-utils (pdftoppm)
/// </summary>
public class TesseractOcrService : IOCRService
{
    private readonly ILogger<TesseractOcrService> _logger;
    private readonly string _tessDataPath;

    public TesseractOcrService(ILogger<TesseractOcrService> logger)
    {
        _logger = logger;
        // Auto-detect tessdata from common install paths
        _tessDataPath = FindTessDataPath();
    }

    public async Task<string?> ExtractTextAsync(byte[] pdfContent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var tempDir = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var pdfPath = Path.Combine(tempDir, "input.pdf");
            await File.WriteAllBytesAsync(pdfPath, pdfContent, cancellationToken);

            // 1. Convert PDF to images using pdftoppm
            _logger.LogInformation("Converting PDF to images via pdftoppm");
            var imagePrefix = Path.Combine(tempDir, "page");
            await RunProcessAsync("pdftoppm", $"-png -r 300 \"{pdfPath}\" \"{imagePrefix}\"", cancellationToken);

            // Get list of generated page images
            // Sort numerically to avoid lexicographic ordering (page-10 before page-2)
            var pageFiles = Directory.GetFiles(tempDir, "page-*.png")
                .OrderBy(f =>
                {
                    var fileName = Path.GetFileNameWithoutExtension(f);
                    var numberPart = fileName.Split('-').Last();
                    return int.TryParse(numberPart, out var n) ? n : 0;
                })
                .ToList();

            if (pageFiles.Count == 0)
            {
                _logger.LogWarning("pdftoppm produced no pages");
                return null;
            }

            _logger.LogInformation("Processing {PageCount} pages with Tesseract", pageFiles.Count);

            // 2. Run Tesseract on each page in parallel
            // Uses SemaphoreSlim to cap concurrency at Environment.ProcessorCount / 2
            // to avoid saturating CPU on large multi-page documents.
            var semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
            var pageTexts = new List<string>();
            var lockObj = new object();

            await Parallel.ForEachAsync(pageFiles, cancellationToken, async (pageFile, ct) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var outputBase = Path.Combine(tempDir, $"out_{Path.GetFileNameWithoutExtension(pageFile)}");
                    var args = $"\"{pageFile}\" \"{outputBase}\" -l eng+rus --psm 3";

                    var envVars = new Dictionary<string, string>
                    {
                        ["TESSDATA_PREFIX"] = _tessDataPath
                    };

                    await RunProcessAsync("tesseract", args, ct, envVars);

                    var textFile = $"{outputBase}.txt";
                    if (File.Exists(textFile))
                    {
                        var text = await File.ReadAllTextAsync(textFile, ct);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            lock (lockObj)
                                pageTexts.Add(text.Trim());
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var result = pageTexts.Count > 0 ? string.Join("\n\n", pageTexts) : null;
            _logger.LogInformation("Tesseract extracted {Length} chars from {Pages} pages",
                result?.Length ?? 0, pageFiles.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Tesseract OCR was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tesseract OCR failed");
            return null;
        }
        finally
        {
            // Cleanup temp files
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private async Task RunProcessAsync(string fileName, string arguments,
        CancellationToken cancellationToken, Dictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (extraEnv != null)
        {
            foreach (var (key, value) in extraEnv)
                psi.EnvironmentVariables[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout/stderr in parallel to avoid buffer deadlock
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdOut = await outputTask;
        var stdErr = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("{File} exited with code {Code}: {Error}",
                fileName, process.ExitCode, stdErr?.Trim());
        }

        if (!string.IsNullOrEmpty(stdErr))
            _logger.LogDebug("{File} stderr: {Error}", fileName, stdErr?.Trim());
    }

    private static string FindTessDataPath()
    {
        var candidates = new[]
        {
            "/usr/share/tesseract-ocr/5/tessdata",
            "/usr/share/tesseract-ocr/4.00/tessdata",
            "/usr/share/tesseract-ocr/4/tessdata",
            "/usr/share/tessdata",
            "/usr/local/share/tessdata"
        };

        foreach (var path in candidates)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "eng.traineddata")))
                return path;
        }

        // Fallback - Tesseract knows its own path
        return string.Empty;
    }
}