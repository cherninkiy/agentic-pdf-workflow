using Microsoft.Extensions.Logging;
using Shared.Interfaces;

namespace Worker.Services;

public class AzureOcrService : IOCRService
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly ILogger<AzureOcrService> _logger;

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
            // Using HttpClient directly to call Azure AI Document Intelligence REST API
            // This avoids pulling in the full Azure SDK for MVP
            using var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri(_endpoint.TrimEnd('/') + "/");
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _apiKey);

            using var content = new ByteArrayContent(pdfContent);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

            // Start the analysis
            var response = await httpClient.PostAsync("formrecognizer/documentModels/prebuilt-read:analyze?api-version=2023-07-31", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var operationLocation = response.Headers.GetValues("Operation-Location").FirstOrDefault();
            if (operationLocation == null)
            {
                _logger.LogError("No Operation-Location header in OCR response");
                return null;
            }

            // Poll for result
            string? result = null;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000, cancellationToken);
                var pollResponse = await httpClient.GetAsync(operationLocation, cancellationToken);
                pollResponse.EnsureSuccessStatusCode();

                var json = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var status = doc.RootElement.GetProperty("status").GetString();

                if (status == "succeeded")
                {
                    var pages = doc.RootElement.GetProperty("analyzeResult").GetProperty("pages");
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
                    _logger.LogError("Azure OCR analysis failed");
                    break;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OCR request failed");
            return null;
        }
    }
}