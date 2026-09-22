namespace ArrImportSolver.Lidarr.Models;

public sealed class QueueResource
{
    public int Id { get; set; }
    public string? DownloadId { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public string? TrackedDownloadStatus { get; set; }
    public string? TrackedDownloadState { get; set; }
    public long Size { get; set; }
    public long Sizeleft { get; set; }
    public string? Timeleft { get; set; }
    public DateTime? EstimatedCompletionTime { get; set; }
    public ArtistResource? Artist { get; set; }
    public AlbumResource? Album { get; set; }
    public QualityModel? Quality { get; set; }
}