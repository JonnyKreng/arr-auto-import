using System.Net;
using System.Text.Json;
using ArrImportSolver.Lidarr;
using ArrImportSolver.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArrImportSolver.Tests;

public class LidarrClientTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public string? LastUri { get; private set; }
        public HttpRequestMessage? LastRequestMessage { get; private set; }
        public List<HttpResponseMessage> Responses { get; } = new();
        private int _index;

        public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            Responses.AddRange(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestMessage = request;
            LastUri = request.RequestUri?.ToString();
            return Responses[_index++ % Math.Max(1, Responses.Count)];
        }
    }

    private static LidarrClient CreateClient(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new LidarrOptions
        {
            Url = "http://localhost:8686",
            Key = "test-api-key"
        });
        var logger = new Mock<ILogger<LidarrClient>>().Object;
        return new LidarrClient(http, new OptionsMonitorStub<LidarrOptions>(options.Value), logger);
    }

    private sealed class OptionsMonitorStub<T> : IOptionsMonitor<T> where T : class
    {
        public OptionsMonitorStub(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task Constructor_AddsApiKeyHeader()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"page\":1,\"pageSize\":100,\"records\":[]}")
        });
        var client = CreateClient(handler);
        await client.GetQueueAsync(CancellationToken.None);
        Assert.NotNull(handler.LastRequestMessage);
        Assert.True(handler.LastRequestMessage.Headers.Contains("X-Api-Key"));
    }

    [Fact]
    public async Task GetQueueAsync_CallsCorrectEndpoint()
    {
        var response = new { page = 1, pageSize = 100, records = Array.Empty<object>() };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
        });
        var client = CreateClient(handler);
        var result = await client.GetQueueAsync(CancellationToken.None);
        Assert.NotNull(handler.LastUri);
        Assert.Contains("/api/v1/queue", handler.LastUri);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReleaseTracksAsync_CallsCorrectEndpoint()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        var client = CreateClient(handler);
        await client.GetReleaseTracksAsync(456, CancellationToken.None);
        Assert.Contains("albumReleaseId=456", handler.LastUri);
    }

    [Fact]
    public async Task GetManualImportAsync_CallsCorrectEndpoint()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        var client = CreateClient(handler);
        await client.GetManualImportAsync("ABC123", CancellationToken.None);
        Assert.Contains("downloadId=ABC123", handler.LastUri);
    }

    [Fact]
    public async Task GetManualImportAsync_EscapesDownloadId()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]")
        });
        var client = CreateClient(handler);
        await client.GetManualImportAsync("ABC/123", CancellationToken.None);
        Assert.Contains("downloadId=ABC%2F123", handler.LastUri);
    }

    [Fact]
    public async Task ImportAsync_PostsCommand()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);
        await client.ImportAsync(new[] { new ArrImportSolver.Lidarr.Models.ManualImportFile { Path = "/test.flac" } }, CancellationToken.None);
        Assert.NotNull(handler.LastRequestMessage);
        Assert.Equal(HttpMethod.Post, handler.LastRequestMessage.Method);
        Assert.Contains("/api/v1/command", handler.LastUri);
    }

    [Fact]
    public async Task DeleteQueueItemAsync_Handles404Gracefully()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);
        await client.DeleteQueueItemAsync(123, true, true, CancellationToken.None);
    }

    [Fact]
    public async Task DeleteQueueItemAsync_CallsCorrectEndpoint()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = CreateClient(handler);
        await client.DeleteQueueItemAsync(123, true, false, CancellationToken.None);
        Assert.Contains("/api/v1/queue/123", handler.LastUri);
        Assert.Contains("removeFromClient=true", handler.LastUri);
        Assert.Contains("blocklist=false", handler.LastUri);
    }
}
