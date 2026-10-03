using System.Net;
using System.Text.Json;
using ArrImportSolver.Options;
using ArrImportSolver.Solver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArrImportSolver.Tests;

public class LlmDecisionClientTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public string? LastRequest { get; private set; }
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
            if (request.Content != null)
            {
                LastRequest = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return Responses[_index++ % Math.Max(1, Responses.Count)];
        }
    }

    private static LlmDecisionClient CreateClient(FakeHttpMessageHandler handler, string? apiKey = null)
    {
        var http = new HttpClient(handler);
        var options = new LidarrOptions
        {
            Llm = new LlmOptions
            {
                Url = "http://localhost:11434",
                ApiKey = apiKey,
                Model = "llama3.2",
                MaxTokens = 100,
                TimeoutSeconds = 30,
                MaxRetries = 0,
                RetryDelaySeconds = 0
            }
        };
        var logger = new Mock<ILogger<LlmDecisionClient>>().Object;
        return new LlmDecisionClient(http, new OptionsMonitorStub<ArrImportSolver.Options.LidarrOptions>(options), logger);
    }

    [Fact]
    public async Task DecideAsync_HandlesChoiceResponse()
    {
        var response = new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "{\"choice\":\"option_0\",\"confidence\":0.95}" }, finish_reason = "stop" }
            }
        };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            State = new { file_name = "test.flac" },
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?> { ["option_0"] = "track" } }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        Assert.True(result.Answers.ContainsKey("mapping"));
        Assert.Equal("option_0", result.Answers["mapping"].Choice);
        Assert.Equal(0.95, result.Answers["mapping"].Confidence);
    }

    [Fact]
    public async Task DecideAsync_HandlesNoulResponse()
    {
        var response = new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "{\"noul\":0.8,\"confidence\":0.9}" }, finish_reason = "stop" }
            }
        };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["reject"] = new() { Type = "noul", Instructions = "reject?" }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        Assert.Equal(0.8, result.Answers["reject"].Noul);
        Assert.Equal(0.9, result.Answers["reject"].Confidence);
    }

    [Fact]
    public async Task DecideAsync_SetsAuthHeaderWhenApiKeyProvided()
    {
        var response = new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "{\"choice\":\"option_0\",\"confidence\":0.95}" }, finish_reason = "stop" }
            }
        };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler, apiKey: "test-key");
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?>() }
            }
        };
        await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(handler.LastRequestMessage);
        Assert.True(handler.LastRequestMessage.Headers.Authorization?.Scheme == "Bearer");
        Assert.Equal("test-key", handler.LastRequestMessage.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task DecideAsync_AddsBearerOnlyWhenApiKeyNonEmpty()
    {
        var response = new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "{\"choice\":\"option_0\",\"confidence\":0.95}" }, finish_reason = "stop" }
            }
        };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler, apiKey: "");
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?>() }
            }
        };
        await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(handler.LastRequestMessage);
        Assert.Null(handler.LastRequestMessage.Headers.Authorization);
    }

    [Fact]
    public async Task DecideAsync_ReturnsErrorOnNonSuccessStatus()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?>() }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("LLM returned 400", result.Error);
    }

    [Fact]
    public async Task DecideAsync_ReturnsErrorWhenNoChoices()
    {
        var response = new { choices = Array.Empty<object>() };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?>() }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("No choices", result.Error);
    }

    [Fact]
    public async Task DecideAsync_HandlesMalformedJson()
    {
        var response = new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content = "not json" }, finish_reason = "stop" }
            }
        };
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response))
        });
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map it", Criteria = new Dictionary<string, string?>() }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("Failed to parse JSON", result.Error);
    }

    [Fact]
    public async Task DecideAsync_AsksMultipleQuestionsSeparately()
    {
        var resp1 = new { choices = new[] { new { message = new { content = "{\"choice\":\"option_0\",\"confidence\":0.9}" } } } };
        var resp2 = new { choices = new[] { new { message = new { content = "{\"noul\":0.3,\"confidence\":0.9}" } } } };
        var handler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(resp1)) },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(resp2)) }
        );
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice", Instructions = "map", Criteria = new Dictionary<string, string?>() },
                ["reject"] = new() { Type = "noul", Instructions = "reject" }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Answers.Count);
    }

    private sealed class OptionsMonitorStub<T> : Microsoft.Extensions.Options.IOptionsMonitor<T> where T : class
    {
        public OptionsMonitorStub(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public System.IDisposable? OnChange(System.Action<T, string?> listener) => null;
    }

    [Fact]
    public async Task DecideAsync_ShortCircuitsOnError()
    {
        var bad = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var good = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        var handler = new FakeHttpMessageHandler(bad, good);
        var client = CreateClient(handler);
        var request = new LlmDecideRequest
        {
            Questions = new Dictionary<string, QuestionDefinition>
            {
                ["mapping"] = new() { Type = "choice" },
                ["reject"] = new() { Type = "noul" }
            }
        };
        var result = await client.DecideAsync(request, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Contains("question 'mapping'", result.Error);
    }

}
