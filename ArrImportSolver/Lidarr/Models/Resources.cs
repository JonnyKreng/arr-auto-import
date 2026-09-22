namespace ArrImportSolver.Lidarr.Models;

public sealed class ArtistResource
{
    public int Id { get; set; }
    public string? ArtistName { get; set; }
    public string? ForeignArtistId { get; set; }
    public string? Status { get; set; }
}

public sealed class AlbumResource
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public string? ForeignAlbumId { get; set; }
    public string? ReleaseDate { get; set; }
    public string? AlbumType { get; set; }
    public List<AlbumReleaseResource>? Releases { get; set; }
}

public sealed class AlbumReleaseResource
{
    public int? Id { get; set; }
    public string? ForeignReleaseId { get; set; }
    public string? Title { get; set; }
    public string? ReleaseDate { get; set; }
    public List<string>? Label { get; set; }
    public List<string>? Country { get; set; }
    public bool? Monitored { get; set; }
}

public sealed class QualityModel
{
    public Quality Quality { get; set; } = new();
    public Revision Revision { get; set; } = new();
}

public sealed class Quality
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
    public int Resolution { get; set; }
    public int Modifier { get; set; }
}

public sealed class Revision
{
    public int Version { get; set; }
    public int Real { get; set; }
    public bool IsRepack { get; set; }
}

public sealed class Rejection
{
    public string? Reason { get; set; }
    public string? Type { get; set; }
}