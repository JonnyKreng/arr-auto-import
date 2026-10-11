namespace ArrImportSolver.Options;

public class LidarrOptions
{
    public const string SectionName = "Lidarr";

    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public LlmOptions Llm { get; set; } = new();
    public LidarrModelOptions Model { get; set; } = new();
}

public class LlmOptions
{
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 600;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Context window (in tokens) to request from Ollama via the num_ctx option. Ollama defaults to
    /// a small context; a mapping prompt with hundreds of options plus the answer budget can exceed
    /// it, which silently truncates the input. 0 sends no num_ctx and uses the server default.
    /// </summary>
    public int NumCtx { get; set; } = 32768;
}

public class LidarrModelOptions
{
    public double MinConfidence { get; set; } = 0.85;
}

public class UiOptions
{
    public const string SectionName = "Ui";

    public int Port { get; set; } = 8080;
}