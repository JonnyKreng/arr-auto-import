using System.Text.Json.Serialization;

namespace ArrImportSolver.Solver;

public sealed class LayaDecideRequest
{
    public object? State { get; set; }
    public Dictionary<string, LayaQuestion> Questions { get; set; } = new();
}

public sealed class LayaQuestion
{
    public string Type { get; set; } = "";
    public string Instructions { get; set; } = "";
    public Dictionary<string, string?>? Criteria { get; set; }
}

public sealed class LayaDecideResponse
{
    public string? Model { get; set; }
    public Dictionary<string, LayaAnswer> Answers { get; set; } = new();
    public Dictionary<string, object>? Routing { get; set; }
    public Dictionary<string, object>? Usage { get; set; }

    [JsonPropertyName("latency_ms")] public double? LatencyMs { get; set; }

    public string? Error { get; set; }
}

public sealed class LayaAnswer
{
    public string? Type { get; set; }

    public string? Choice { get; set; }

    public Dictionary<string, double>? Probabilities { get; set; }

    public double Confidence { get; set; }

    public double? Noul { get; set; }

    public double? Score { get; set; }
}