using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace IntegrationTests;

/// <summary>
/// Integration tests that exercise the full PDF processing workflow
/// using Testcontainers for PostgreSQL and RabbitMQ.
///
/// Tests require Docker to be running on the host.
/// </summary>
public class FullWorkflowTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Start PostgreSQL container
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("pdf_processing")
            .WithUsername("pdf_user")
            .WithPassword("pdf_password")
            .WithCleanUp(true)
            .Build();

        await _postgres.StartAsync();

        // Start RabbitMQ container
        _rabbitMq = new RabbitMqBuilder()
            .WithImage("rabbitmq:4-management")
            .WithCleanUp(true)
            .Build();

        await _rabbitMq.StartAsync();

        // Create WebApplicationFactory pointing to ApiGateway with
        // PostgreSQL and RabbitMQ connection strings
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings__DefaultConnection", _postgres.GetConnectionString());
                builder.UseSetting("RabbitMq__Host", _rabbitMq.Hostname);
                builder.UseSetting("RabbitMq__Username", "guest");
                builder.UseSetting("RabbitMq__Password", "guest");
                builder.UseSetting("Storage__LocalPath", Path.GetTempPath());
            });

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        await _rabbitMq.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Upload_And_List_Workflow()
    {
        // Setup
        var filePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "Invoice.pdf");
        filePath = Path.GetFullPath(filePath);
        Assert.True(File.Exists(filePath), $"Sample PDF not found at {filePath}");

        await using var fileStream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "file", "Invoice.pdf");

        // Act: Upload
        var uploadResponse = await _client.PostAsync("/upload", content);
        Assert.Equal(HttpStatusCode.Accepted, uploadResponse.StatusCode);

        var uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        var uploadResult = JsonSerializer.Deserialize<JsonElement>(uploadBody);
        var documentId = uploadResult.GetProperty("documentId").GetGuid();
        Assert.NotEqual(Guid.Empty, documentId);

        // Act: List
        var listResponse = await _client.GetAsync("/list");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var listBody = await listResponse.Content.ReadAsStringAsync();
        var listResult = JsonSerializer.Deserialize<JsonElement>(listBody);
        Assert.True(listResult.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Upload_BadRequest_ForNonPdf()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("not a pdf", Encoding.UTF8), "file", "test.txt");

        var response = await _client.PostAsync("/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_BadRequest_ForEmptyFile()
    {
        var response = await _client.PostAsync("/upload", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetText_ReturnsNotFound_ForMissingId()
    {
        var response = await _client.GetAsync($"/text/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}