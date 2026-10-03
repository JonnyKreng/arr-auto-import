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