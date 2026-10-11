using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrImportSolver.Options;
using Microsoft.Extensions.Options;

namespace ArrImportSolver.Solver;

public class LlmDecisionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<LidarrOptions> _options;
    private readonly ILogger<LlmDecisionClient> _logger;

    public LlmDecisionClient(HttpClient http, IOptionsMonitor<LidarrOptions> options,
        ILogger<LlmDecisionClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<LlmDecideResponse?> DecideAsync(LlmDecideRequest request, CancellationToken ct)
    {
        var response = new LlmDecideResponse
        {
            Answers = new Dictionary<string, LlmAnswer>()
        };

        foreach (var (qid, qdef) in request.Questions)
        {
            var single = new LlmDecideRequest
            {
                State = request.State,
                Questions = new Dictionary<string, QuestionDefinition> { [qid] = qdef }
            };

            var outcome = await AskAsync(single, qid, qdef, ct);
            if (outcome.Error is not null)
            {
                return new LlmDecideResponse { Error = $"question '{qid}': {outcome.Error}" };
            }

            response.Answers[qid] = outcome.Answer!;
        }

        return response;
    }

    private async Task<AskOutcome> AskAsync(LlmDecideRequest request, string qid, QuestionDefinition qdef,
        CancellationToken ct)
    {
        var opts = _options.CurrentValue.Llm;
        var url = $"{opts.Url.TrimEnd('/')}/v1/chat/completions";

        var messages = BuildMessages(request);
        var payload = new
        {
            model = opts.Model,
            messages,
            temperature = 0,
            max_tokens = opts.MaxTokens,
            response_format = new { type = "json_object" }
        };

        var requestBody = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        HttpRequestMessage? requestMessage = null;
        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
            requestMessage.Content = content;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(opts.TimeoutSeconds));

            using var httpResponse = requestMessage != null
                ? await _http.SendAsync(requestMessage, timeout.Token)
                : await _http.PostAsync(url, content, timeout.Token);
            if (httpResponse.IsSuccessStatusCode)
            {
                // Read the body as text first: Ollama (and proxies in front of it) happily answer 200
                // with an HTML error page or an error envelope, and ReadFromJsonAsync would turn that
                // into an opaque JsonException instead of a diagnosable message.
                var body = await httpResponse.Content.ReadAsStringAsync(timeout.Token);
                var outcome = ParseAnswer(body, qid, qdef);
                if (outcome.Error is not null)
                {
                    _logger.LogWarning("LLM answer for question {QuestionId} at {Url} failed: {Error}; body: {Body}",
                        qid, url, outcome.Error, Truncate(body, 500));
                }

                return outcome;
            }

            var detail = await httpResponse.Content.ReadAsStringAsync(timeout.Token);
            _logger.LogWarning("LLM returned {Status} at {Url} for question {QuestionId}: {Detail}",
                (int)httpResponse.StatusCode, url, qid, detail);
            return new AskOutcome { Error = $"LLM returned {(int)httpResponse.StatusCode}" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLM timed out after {Timeout}s for question {QuestionId}", opts.TimeoutSeconds, qid);
            return new AskOutcome { Error = $"LLM timed out after {opts.TimeoutSeconds}s" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM call failed at {Url} for question {QuestionId}", url, qid);
            return new AskOutcome { Error = ex.Message };
        }
    }

    private static List<Dictionary<string, string>> BuildMessages(LlmDecideRequest request)
    {
        var stateText = request.State is null ? "null" : JsonSerializer.Serialize(request.State, JsonOptions);
        var messages = new List<Dictionary<string, string>>
        {
            new() { ["role"] = "system", ["content"] = SystemPrompt }
        };

        foreach (var (_, qdef) in request.Questions)
        {
            var userContent = BuildUserMessage(stateText, qdef);
            messages.Add(new() { ["role"] = "user", ["content"] = userContent });
        }

        return messages;
    }

    private static string BuildUserMessage(string stateText, QuestionDefinition qdef)
    {
        var instructions = qdef.Instructions ?? "";
        var criteria = qdef.Criteria;

        if (qdef.Type == "noul")
        {
            var falseText = criteria?.GetValueOrDefault("false") ?? "";
            var trueText = criteria?.GetValueOrDefault("true") ?? "";

            var parts = new List<string>
            {
                $"Download context (JSON):\n{stateText}",
                $"Include this download into the target album.\nQuestion:\n{instructions}",
                "Interpretation:\n" +
                (string.IsNullOrEmpty(falseText) ? "" : $"- false: {falseText}") +
                (string.IsNullOrEmpty(trueText) ? "" : $"\n- true: {trueText}"),
                """Return JSON: {"noul": <0..1>, "confidence": <0..1>}. "noul" is the probability that the "true" statement applies (0 = definitely false, 1 = definitely true). "confidence" is how sure you are of this answer (0..1)."""
            };

            return string.Join("\n\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
        else
        {
            // Filter on the criteria value itself: splitting the rendered line on ": " would also
            // break on keys or descriptions that contain ": ".
            var optionsLines = criteria?
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .Select(kvp => $"- {kvp.Key}: {kvp.Value}")
                .ToList() ?? new List<string>();

            var parts = new List<string>
            {
                $"Download context (JSON):\n{stateText}",
                $"Include this download into the target album.\nQuestion:\n{instructions}",
                "Options (key: candidate). Answer with the KEY of the option you choose:\n" +
                string.Join("\n", optionsLines),
                """Return JSON: {"choice": "<key>", "confidence": <0..1>}. "confidence" is how sure you are (1.0 = certain). If none of the options fits the download, choose "no_match". Never invent a key - use one of the keys listed above."""
            };

            return string.Join("\n\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    private static AskOutcome ParseAnswer(string body, string qid, QuestionDefinition qdef)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AskOutcome { Error = "Empty response body from LLM" };
        }

        LlmChatResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<LlmChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new AskOutcome
            {
                Error = $"LLM response was not JSON ({ex.Message}); body started with: {Truncate(body, 120)}"
            };
        }

        if (result?.Choices is null || result.Choices.Count == 0)
        {
            // Ollama reports "model not loaded" and friends with a 200 + {"error": ...} envelope.
            var envelope = TryReadErrorEnvelope(body);
            return new AskOutcome
            {
                Error = envelope ?? $"No choices in LLM response; body started with: {Truncate(body, 120)}"
            };
        }

        var choice = result.Choices[0];
        var content = choice.Message?.GetContent();

        if (string.IsNullOrWhiteSpace(content))
        {
            return new AskOutcome
            {
                Error =
                    $"Empty content in LLM response (finish_reason={choice.EffectiveFinishReason ?? "unknown"}); body started with: {Truncate(body, 120)}"
            };
        }

        var answerKeys = qdef.Type == "noul" ? new[] { "noul" } : new[] { "choice" };
        var parsed = ExtractJson(content, answerKeys);
        if (parsed is null)
        {
            return new AskOutcome
            {
                Error =
                    $"Failed to parse JSON from LLM response (finish_reason={choice.EffectiveFinishReason ?? "unknown"}, content length {content.Length}): {Truncate(content, 120)}"
            };
        }

        if (qdef.Type == "noul")
        {
            var noulObj = parsed.GetValueOrDefault("noul", 0.5);
            var noul = ToDouble(noulObj, 0.5);
            var confidenceObj = parsed.GetValueOrDefault("confidence", Math.Abs(2.0 * noul - 1.0));
            var confidence = ToDouble(confidenceObj, Math.Abs(2.0 * noul - 1.0));
            return new AskOutcome
            {
                Answer = new LlmAnswer
                {
                    Type = "noul",
                    Noul = Math.Round(Clamp(noul, 0.0, 1.0), 4),
                    Confidence = Math.Round(Clamp(confidence, 0.0, 1.0), 4),
                    Raw = content
                }
            };
        }

        var answerChoice = parsed.GetValueOrDefault("choice")?.ToString()?.Trim() ?? "";
        var answerConfidence = ToDouble(parsed.GetValueOrDefault("confidence"), 0.0);

        if (answerChoice.Length == 0)
        {
            return new AskOutcome
            {
                Error = $"LLM answer for '{qid}' had no 'choice' key; content: {Truncate(content, 120)}"
            };
        }

        // A key that was never offered means the model hallucinated it (or echoed the label instead
        // of the key). Importing on that would attach the file to an arbitrary candidate, so fail
        // loudly and let the Worker leave the row to a human instead.
        var knownKeys = qdef.Criteria?.Keys;
        if (knownKeys is { Count: > 0 } && !knownKeys.Contains(answerChoice))
        {
            return new AskOutcome
            {
                Error =
                    $"LLM answer for '{qid}' chose unknown option '{answerChoice}'; expected one of: {string.Join(", ", knownKeys)}"
            };
        }

        return new AskOutcome
        {
            Answer = new LlmAnswer
            {
                Type = "choice",
                Choice = answerChoice,
                Confidence = Math.Round(Clamp(answerConfidence, 0.0, 1.0), 4),
                Raw = content
            }
        };
    }

    private static Dictionary<string, object>? ExtractJson(string text, IReadOnlyCollection<string> answerKeys)
    {
        Dictionary<string, object>? firstObject = null;

        // Scan every balanced top-level object instead of stopping at the first one. Models often
        // prefix their answer with a bit of prose that itself contains braces (an example snippet,
        // a quoted JSON fragment), and that object must not win over the real answer.
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            var end = FindObjectEnd(text, i);
            if (end < 0) return firstObject;

            var parsed = TryDeserializeObject(text[i..(end + 1)]);
            if (parsed is not null)
            {
                if (answerKeys.Any(parsed.ContainsKey)) return parsed;
                firstObject ??= parsed;
            }

            i = end + 1;
        }

        return firstObject;
    }

    /// <summary>Index of the '}' closing the object that starts at <paramref name="start"/>, or -1.</summary>
    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    break;
            }

            // depth 0 with a ']' means we ran into the end of an enclosing array before the object
            // closed - the slice is truncated, so stop rather than mis-read it.
            if (depth == 0) return c == '}' ? i : -1;
        }

        return -1;
    }

    private static Dictionary<string, object>? TryDeserializeObject(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads Ollama's {"error": "..."} envelope, which it returns with a 200 status.</summary>
    private static string? TryReadErrorEnvelope(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("error", out var error)) return null;
            var text = error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : $"LLM reported an error: {Truncate(text, 200)}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    private static double Clamp(double value, double min, double max)
    {
        return value < min ? min : (value > max ? max : value);
    }

    private static double ToDouble(object? value, double fallback)
    {
        switch (value)
        {
            case null:
                return fallback;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number when element.TryGetDouble(out var number) => number,
                    JsonValueKind.String when double.TryParse(element.GetString(), out var parsedNumber) =>
                        parsedNumber,
                    JsonValueKind.True => 1d,
                    JsonValueKind.False => 0d,
                    _ => fallback
                };
            case double d:
                return d;
            case int i:
                return i;
            case long l:
                return l;
            case decimal m:
                return (double)m;
            case string s when double.TryParse(s, out var parsedText):
                return parsedText;
            default:
                try
                {
                    return Convert.ToDouble(value);
                }
                catch
                {
                    return fallback;
                }
        }
    }

    private const string SystemPrompt = """
        You are the import decision engine of a music library (Lidarr). A downloaded music
        file must be matched against the album the library wants to import it into. You answer
        focused questions about that match. Always respond with a single JSON object and
        nothing else - no markdown, no prose.
        """;

    private sealed class AskOutcome
    {
        public LlmAnswer? Answer { get; set; }

        public string? Error { get; set; }
    }
}

public class LlmDecideRequest
{
    public object? State { get; set; }
    public Dictionary<string, QuestionDefinition> Questions { get; set; } = new();
}

public class LlmDecideResponse
{
    public string? Error { get; set; }
    public Dictionary<string, LlmAnswer> Answers { get; set; } = new();
}

public class LlmAnswer
{
    public string Type { get; set; } = "choice";
    public string? Choice { get; set; }
    public double? Noul { get; set; }
    public double Confidence { get; set; }

    /// <summary>Raw message content returned by the model, for diagnostics.</summary>
    public string? Raw { get; set; }
}

internal class LlmChatResponse
{
    public List<LlmChoice>? Choices { get; set; }
}

internal class LlmChoice
{
    public LlmMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }

    [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }

    /// <summary>Reason to report in diagnostics: Ollama uses finish_reason, some proxies stop_reason.</summary>
    [JsonIgnore]
    public string? EffectiveFinishReason => FinishReason ?? StopReason;
}

internal class LlmMessage
{
    public string? Role { get; set; }

    /// <summary>
    /// Plain string for most providers; OpenAI-compatible endpoints may instead send an array of
    /// content parts, so the raw JSON element is kept and unwrapped by <see cref="GetContent"/>.
    /// </summary>
    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }

    public string? GetContent()
    {
        if (Content is { ValueKind: JsonValueKind.String } element)
        {
            return element.GetString();
        }

        if (Content is { ValueKind: JsonValueKind.Array } array)
        {
            var parts = new List<string>();
            foreach (var part in array.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    parts.Add(part.GetString() ?? "");
                }
                else if (part.ValueKind == JsonValueKind.Object &&
                         part.TryGetProperty("text", out var text) &&
                         text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? "");
                }
            }

            var joined = string.Concat(parts);
            if (!string.IsNullOrWhiteSpace(joined)) return joined;
        }

        // Reasoning models can emit the whole answer into reasoning_content with an empty content.
        return ReasoningContent;
    }
}
