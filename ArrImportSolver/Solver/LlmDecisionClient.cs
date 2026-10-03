using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpRequestMessage? requestMessage = null;
        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
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
                var result = await httpResponse.Content.ReadFromJsonAsync<LlmChatResponse>(JsonOptions, timeout.Token);
                return ParseAnswer(result, qid, qdef);
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

        foreach (var (qid, qdef) in request.Questions)
        {
            var userContent = BuildUserMessage(qid, stateText, qdef);
            messages.Add(new() { ["role"] = "user", ["content"] = userContent });
        }

        return messages;
    }

    private static string BuildUserMessage(string qid, string stateText, QuestionDefinition qdef)
    {
        var instructions = qdef.Instructions ?? "";
        var criteria = qdef.Criteria;

        if (qdef.Type == "noul")
        {
            var falseText = criteria?.GetValueOrDefault("false") as string ?? "";
            var trueText = criteria?.GetValueOrDefault("true") as string ?? "";

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
            var optionsLines = criteria?.Select(kvp => $"- {kvp.Key}: {kvp.Value}")
                .Where(l => !string.IsNullOrEmpty(l.Split(": ")[1]))
                .ToList() ?? new List<string>();

            var parts = new List<string>
            {
                $"Download context (JSON):\n{stateText}",
                $"Include this download into the target album.\nQuestion:\n{instructions}",
                "Options (key: candidate). Answer with the KEY of the option you choose:\n" + string.Join("\n", optionsLines),
                """Return JSON: {"choice": "<key>", "confidence": <0..1>}. "confidence" is how sure you are (1.0 = certain). If none of the options fits the download, choose "no_match". Never invent a key - use one of the keys listed above."""
            };

            return string.Join("\n\n", parts.Where(p => !string.IsNullOrEmpty(p)));
        }
    }

    private static AskOutcome ParseAnswer(LlmChatResponse? result, string qid, QuestionDefinition qdef)
    {
        if (result?.Choices is null || result.Choices.Count == 0)
        {
            return new AskOutcome { Error = "No choices in LLM response" };
        }

        var choice = result.Choices[0];
        var content = choice.Message?.Content;

        if (string.IsNullOrEmpty(content))
        {
            return new AskOutcome
            {
                Error = $"Empty content in LLM response (finish_reason={choice.FinishReason ?? "unknown"})"
            };
        }

        var parsed = ExtractJson(content);
        if (parsed is null)
        {
            return new AskOutcome
            {
                Error =
                    $"Failed to parse JSON from LLM response (finish_reason={choice.FinishReason ?? "unknown"}, content length {content.Length})"
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
                    Confidence = Math.Round(Clamp(confidence, 0.0, 1.0), 4)
                }
            };
        }

        var answerChoice = parsed.GetValueOrDefault("choice")?.ToString() ?? "";
        var answerConfidence = ToDouble(parsed.GetValueOrDefault("confidence"), 0.0);

        return new AskOutcome
        {
            Answer = new LlmAnswer
            {
                Type = "choice",
                Choice = answerChoice,
                Confidence = Math.Round(Clamp(answerConfidence, 0.0, 1.0), 4)
            }
        };
    }

    private static Dictionary<string, object>? ExtractJson(string text)
    {
        var firstBrace = text.IndexOf('{');
        if (firstBrace < 0) return null;

        var depth = 0;
        for (int i = firstBrace; i < text.Length; i++)
        {
            if (text[i] == '{' || text[i] == '[') depth++;
            else if (text[i] == '}' || text[i] == ']') depth--;

            if (depth == 0)
            {
                try
                {
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(text[firstBrace..(i + 1)], JsonOptions);
                }
                catch
                {
                    return null;
                }
            }
        }
        return null;
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

public class QuestionDefinition
{
    public string Type { get; set; } = "choice";
    public string? Instructions { get; set; }
    public Dictionary<string, string>? Criteria { get; set; }
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
}

internal class LlmChatResponse
{
    public List<LlmChoice>? Choices { get; set; }
}

internal class LlmChoice
{
    public LlmMessage? Message { get; set; }

    public string? FinishReason { get; set; }
}

internal class LlmMessage
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}
