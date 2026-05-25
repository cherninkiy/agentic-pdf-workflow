using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ApiGateway.UnitTests;

/// <summary>
/// Smoke tests that verify the API Gateway is configured correctly.
/// These test basic routing only — full integration tests require PostgreSQL + RabbitMQ.
/// </summary>
public class SmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetList_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/list");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutFile_ReturnsBadRequest()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/upload", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetText_ForMissingId_ReturnsNotFound()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/text/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}