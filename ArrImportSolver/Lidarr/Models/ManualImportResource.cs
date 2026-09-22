using System.Text.Json;

namespace ArrImportSolver.Lidarr.Models;

public sealed class ManualImportResource
{
    public int Id { get; set; }
    public string? Path { get; set; }
    public string? RelativePath { get; set; }
    public string? Name { get; set; }
    public long Size { get; set; }
    public ArtistResource? Artist { get; set; }
    public AlbumResource? Album { get; set; }
    public List<AlbumReleaseResource>? AlbumReleases { get; set; }
    public int? AlbumReleaseId { get; set; }
    public QualityModel? Quality { get; set; }
    public List<Rejection>? Rejections { get; set; }
    public List<int>? TrackIds { get; set; }
    public List<TrackResource>? Tracks { get; set; }
    public string? DownloadId { get; set; }
    public string? FolderName { get; set; }
    public string? SourceFolder { get; set; }
    public bool AdditionalFile { get; set; }
    public bool IsManualImport { get; set; }
}

public sealed class TrackResource
{
    public int Id { get; set; }
    public int? AlbumId { get; set; }
    public string? Title { get; set; }
    public JsonElement? TrackNumber { get; set; }
    public JsonElement? AbsoluteTrackNumber { get; set; }
    public int Duration { get; set; }
    public int? MediumNumber { get; set; }
    public bool HasFile { get; set; }
}

public sealed class ManualImportUpdateResource
{
    public int? Id { get; set; }
    public int? ArtistId { get; set; }
    public int? AlbumId { get; set; }
    public int? AlbumReleaseId { get; set; }
    public QualityModel? Quality { get; set; }
    public List<int>? TrackIds { get; set; }
    public string? ImportMode { get; set; }
}