using Microsoft.Extensions.Logging;
using Shared.Interfaces;

namespace Worker.Services;

public class AzureOcrService : IOCRService
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly ILogger<AzureOcrService> _logger;
    private static readonly HttpClient _httpClient = new();

    public AzureOcrService(ILogger<AzureOcrService> logger, string? endpoint = null, string? apiKey = null)
    {
        _logger = logger;
        _endpoint = endpoint;
        _apiKey = apiKey;
    }

    public async Task<string?> ExtractTextAsync(byte[] pdfContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_endpoint) || string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("Azure OCR credentials not configured, skipping OCR");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_endpoint.TrimEnd('/') + "/formrecognizer/documentModels/prebuilt-read:analyze?api-version=2023-07-31"));
            request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);
            request.Content = new ByteArrayContent(pdfContent);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

            // Start the analysis
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var operationLocation = response.Headers.GetValues("Operation-Location").FirstOrDefault();
            if (operationLocation == null)
            {
                _logger.LogError("No Operation-Location header in OCR response");
                return null;
            }

            // Poll for result with max 30 attempts at 1s intervals
            string? result = null;
            for (int i = 0; i < 30; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken);

                var pollResponse = await _httpClient.GetAsync(operationLocation, cancellationToken);
                pollResponse.EnsureSuccessStatusCode();

                var json = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var status = doc.RootElement.GetProperty("status").GetString();

                if (status == "succeeded")
                {
                    if (!doc.RootElement.TryGetProperty("analyzeResult", out var analyzeResult) ||
                        !analyzeResult.TryGetProperty("pages", out var pages))
                    {
                        _logger.LogWarning("OCR succeeded but no pages found in result");
                        break;
                    }

                    var texts = new List<string>();
                    foreach (var page in pages.EnumerateArray())
                    {
                        if (page.TryGetProperty("lines", out var lines))
                        {
                            foreach (var line in lines.EnumerateArray())
                            {
                                var lineText = line.GetProperty("content").GetString();
                                if (!string.IsNullOrWhiteSpace(lineText))
                                    texts.Add(lineText);
                            }
                        }
                    }

                    result = texts.Count > 0 ? string.Join("\n", texts) : null;
                    break;
                }

                if (status == "failed")
                {
                    var errorMessage = "Azure OCR analysis failed";
                    if (doc.RootElement.TryGetProperty("error", out var error))
                    {
                        errorMessage = error.TryGetProperty("message", out var msg) ? msg.GetString()! : errorMessage;
                    }
                    _logger.LogError("{Error}", errorMessage);
                    break;
                }

                if (i == 29)
                {
                    _logger.LogWarning("Azure OCR polling timed out after 30 seconds");
                }
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Azure OCR was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OCR request failed");
            return null;
        }
    }
}