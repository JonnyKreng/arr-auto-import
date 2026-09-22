namespace ArrImportSolver.Options;

public class LidarrOptions
{
    public const string SectionName = "Lidarr";

    public string Url { get; set; } = "";
    public string Key { get; set; } = "";
    public bool DryRun { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    public string SidecarUrl { get; set; } = "http://sidecar:8000";
    public LidarrModelOptions Model { get; set; } = new();
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