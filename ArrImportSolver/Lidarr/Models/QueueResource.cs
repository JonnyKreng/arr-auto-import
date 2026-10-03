namespace ArrImportSolver.Lidarr.Models;

public sealed class QueueResource
{
    public int Id { get; set; }
    public string? DownloadId { get; set; }
    public string? Title { get; set; }
    public string? Status { get; set; }
    public long Sizeleft { get; set; }
    public string? Timeleft { get; set; }
    public ArtistResource? Artist { get; set; }
    public AlbumResource? Album { get; set; }
}