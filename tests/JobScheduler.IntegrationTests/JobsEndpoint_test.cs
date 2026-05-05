using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JobScheduler.Core.Enums;

namespace JobScheduler.IntegrationTests;

public class JobsEndpoint_test(JobsApiFactory factory) : IClassFixture<JobsApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Arrange
    private static object EmailJobRequest() => new
    {
        type = "Email",
        payload = new { to = "test@example.com", subject = "Hello", body = "World" }
    };

    [Fact]
    public async Task CreateJob_ValidRequest_ReturnsCreated()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/jobs", EmailJobRequest());

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"Pending\"");
    }

    [Fact]
    public async Task GetJob_ExistingId_ReturnsJob()
    {
        // Arrange
        var created = await _client.PostAsJsonAsync("/jobs", EmailJobRequest());
        var createdBody = await created.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString();

        // Act
        var response = await _client.GetAsync($"/jobs/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(id);
    }

    [Fact]
    public async Task GetJob_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync($"/jobs/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelJob_PendingJob_ReturnsNoContent()
    {
        // Arrange
        var created = await _client.PostAsJsonAsync("/jobs", EmailJobRequest());
        var createdBody = await created.Content.ReadAsStringAsync();
        var id = JsonDocument.Parse(createdBody).RootElement.GetProperty("id").GetString();

        // Act
        var response = await _client.DeleteAsync($"/jobs/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ListJobs_FilterByStatus_ReturnsMatchingJobs()
    {
        // Arrange
        await _client.PostAsJsonAsync("/jobs", EmailJobRequest());

        // Act
        var response = await _client.GetAsync("/jobs?status=Pending");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        var jobs = JsonDocument.Parse(body).RootElement.EnumerateArray().ToList();
        jobs.Should().NotBeEmpty();
        jobs.Should().AllSatisfy(j =>
            j.GetProperty("status").GetString().Should().Be("Pending"));
    }

    [Fact]
    public async Task CancelJob_NonExistentId_ReturnsNotFound()
    {
        // Act
        var response = await _client.DeleteAsync($"/jobs/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
