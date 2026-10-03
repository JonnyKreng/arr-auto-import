using System.Text.Json;
using ArrImportSolver.Lidarr.Models;
using ArrImportSolver.Solver;

namespace ArrImportSolver.Tests;

public class ImportDecisionEngineTests
{
    private readonly ImportDecisionEngine _engine = new();

    [Fact]
    public void Classify_ReturnsAdditionalFile_WhenRowIsAdditionalFile()
    {
        var row = new ManualImportResource { AdditionalFile = true };
        var result = _engine.Classify(new[] { row });
        Assert.Single(result);
        Assert.Equal(RowKind.AdditionalFile, result[0].Kind);
        Assert.Same(row, result[0].Row);
    }

    [Fact]
    public void Classify_ReturnsResolved_WhenFullyParsedWithNoRejections()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection>(),
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Quality = new QualityModel()
        };
        var result = _engine.Classify(new[] { row });
        Assert.Single(result);
        Assert.Equal(RowKind.Resolved, result[0].Kind);
    }

    [Fact]
    public void Classify_ReturnsAmbiguous_WhenRejectionsPresent()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection> { new() },
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Quality = new QualityModel()
        };
        var result = _engine.Classify(new[] { row });
        Assert.Equal(RowKind.Ambiguous, result[0].Kind);
    }

    [Fact]
    public void Classify_ReturnsAmbiguous_WhenArtistMissing()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection>(),
            Artist = null,
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Quality = new QualityModel()
        };
        var result = _engine.Classify(new[] { row });
        Assert.Equal(RowKind.Ambiguous, result[0].Kind);
    }

    [Fact]
    public void Classify_ReturnsAmbiguous_WhenAlbumMissing()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection>(),
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = null,
            AlbumReleaseId = 10,
            Quality = new QualityModel()
        };
        var result = _engine.Classify(new[] { row });
        Assert.Equal(RowKind.Ambiguous, result[0].Kind);
    }

    [Fact]
    public void Classify_ReturnsAmbiguous_WhenAlbumReleaseIdMissing()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection>(),
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = null,
            Quality = new QualityModel()
        };
        var result = _engine.Classify(new[] { row });
        Assert.Equal(RowKind.Ambiguous, result[0].Kind);
    }

    [Fact]
    public void Classify_ReturnsAmbiguous_WhenQualityMissing()
    {
        var row = new ManualImportResource
        {
            AdditionalFile = false,
            Rejections = new List<Rejection>(),
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Quality = null
        };
        var result = _engine.Classify(new[] { row });
        Assert.Equal(RowKind.Ambiguous, result[0].Kind);
    }

    private static TrackResource Track(int id, string title, object trackNumber, int durationMs, bool hasFile = false)
    {
        return new TrackResource
        {
            Id = id,
            Title = title,
            TrackNumber = JsonElementFrom(trackNumber),
            Duration = durationMs,
            HasFile = hasFile
        };
    }

    private static JsonElement? JsonElementFrom(object value)
    {
        if (value is null) return null;
        var json = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AlbumReleaseResource Release(int id, string title = "Release", string? releaseDate = null)
    {
        return new AlbumReleaseResource
        {
            Id = id,
            Title = title,
            ReleaseDate = releaseDate,
            ForeignReleaseId = null
        };
    }

    [Fact]
    public async Task BuildCandidatesAsync_ReturnsEmpty_WhenArtistMissing()
    {
        var row = new ManualImportResource { Artist = null, Album = new AlbumResource { Id = 1 } };
        var result = await _engine.BuildCandidatesAsync(row, new Dictionary<int, IReadOnlyList<TrackResource>>(), CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_ReturnsEmpty_WhenAlbumMissing()
    {
        var row = new ManualImportResource { Artist = new ArtistResource { Id = 1 }, Album = null };
        var result = await _engine.BuildCandidatesAsync(row, new Dictionary<int, IReadOnlyList<TrackResource>>(), CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_ReturnsEmpty_WhenNoReleases()
    {
        var row = new ManualImportResource 
        { 
            Artist = new ArtistResource { Id = 1 }, 
            Album = new AlbumResource { Id = 1, Releases = new List<AlbumReleaseResource>() } 
        };
        var result = await _engine.BuildCandidatesAsync(row, new Dictionary<int, IReadOnlyList<TrackResource>>(), CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_BuildsCandidates_OrderedWithTargetReleaseFirst()
    {
        var targetRelease = Release(10, "Target");
        var otherRelease = Release(20, "Other");
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 5 },
            Album = new AlbumResource 
            { 
                Id = 7, 
                Title = "Album", 
                Releases = new List<AlbumReleaseResource> { otherRelease, targetRelease } 
            },
            AlbumReleaseId = 10,
            AlbumReleases = new List<AlbumReleaseResource> { otherRelease, targetRelease }
        };
        var releaseTracks = new Dictionary<int, IReadOnlyList<TrackResource>>
        {
            [10] = new[] { Track(100, "Song One", 1, 240000) },
            [20] = new[] { Track(200, "Song Two", 2, 180000) }
        };
        var result = await _engine.BuildCandidatesAsync(row, releaseTracks, CancellationToken.None);
        Assert.Equal(2, result.Count);
        Assert.Equal("option_0", result[0].Key);
        Assert.Equal(10, result[0].AlbumReleaseId); // target first
        Assert.Equal(5, result[0].ArtistId);
        Assert.Equal(7, result[0].AlbumId);
        Assert.Equal(100, result[0].TrackId);
        Assert.Equal("Song One", result[0].TrackTitle);
        Assert.Equal(1, result[0].TrackNumber);
        Assert.Equal(240000, result[0].TrackDurationMs);
        Assert.Equal("option_1", result[1].Key);
        Assert.Equal(20, result[1].AlbumReleaseId);
    }

    [Fact]
    public async Task BuildCandidatesAsync_DedupesByCandidateKey()
    {
        var release = Release(10);
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1 },
            Album = new AlbumResource { Id = 2, Releases = new List<AlbumReleaseResource> { release } },
            AlbumReleaseId = 10
        };
        var releaseTracks = new Dictionary<int, IReadOnlyList<TrackResource>>
        {
            [10] = new[] { Track(1, "Same Title", 1, 180000), Track(2, "Same Title", 1, 180000) }
        };
        var result = await _engine.BuildCandidatesAsync(row, releaseTracks, CancellationToken.None);
        Assert.Single(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_SkipsReleasesNotInTracksDict()
    {
        var release = Release(10);
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1 },
            Album = new AlbumResource { Id = 2, Releases = new List<AlbumReleaseResource> { release } },
            AlbumReleaseId = 10
        };
        var result = await _engine.BuildCandidatesAsync(row, new Dictionary<int, IReadOnlyList<TrackResource>>(), CancellationToken.None);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_UsesAlbumReleasesWhenAlbumReleasesPopulated()
    {
        var release = Release(10);
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1 },
            Album = new AlbumResource { Id = 2, Releases = new List<AlbumReleaseResource>() },
            AlbumReleases = new List<AlbumReleaseResource> { release },
            AlbumReleaseId = 10
        };
        var releaseTracks = new Dictionary<int, IReadOnlyList<TrackResource>>
        {
            [10] = new[] { Track(1, "Title", 1, 180000) }
        };
        var result = await _engine.BuildCandidatesAsync(row, releaseTracks, CancellationToken.None);
        Assert.Single(result);
    }

    [Fact]
    public async Task BuildCandidatesAsync_ParsesTrackNumberFromString()
    {
        var release = Release(10);
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1 },
            Album = new AlbumResource { Id = 2, Releases = new List<AlbumReleaseResource> { release } },
            AlbumReleaseId = 10
        };
        var releaseTracks = new Dictionary<int, IReadOnlyList<TrackResource>>
        {
            [10] = new[] { Track(1, "Title", "1", 180000) }
        };
        var result = await _engine.BuildCandidatesAsync(row, releaseTracks, CancellationToken.None);
        Assert.Single(result);
        Assert.Equal(1, result[0].TrackNumber);
    }

    [Fact]
    public async Task BuildCandidatesAsync_HandlesNullTrackNumber()
    {
        var release = Release(10);
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1 },
            Album = new AlbumResource { Id = 2, Releases = new List<AlbumReleaseResource> { release } },
            AlbumReleaseId = 10
        };
        var releaseTracks = new Dictionary<int, IReadOnlyList<TrackResource>>
        {
            [10] = new[] { Track(1, "Title", null!, 180000) }
        };
        var result = await _engine.BuildCandidatesAsync(row, releaseTracks, CancellationToken.None);
        Assert.Single(result);
        Assert.Null(result[0].TrackNumber);
    }

    [Fact]
    public void TryResolveDeterministicMatch_ReturnsNull_WhenNoMatches()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Different Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "some_other.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) }
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.Null(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_ReturnsMatch_WhenSingleMatch()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Don't Stop", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "dont_stop.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) }
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
        Assert.Equal("option_0", result.Key);
    }

    [Fact]
    public void TryResolveDeterministicMatch_NormalizesApostrophes()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Don't Stop Believin'", 1, 240000, false);
        var row = new ManualImportResource
        {
            Name = "dont_stop_believin.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 240000) }
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_UsesSuffixMatching()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Track Title", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "Artist - Album - Track Title.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) }
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_RespectsDurationMargin()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "song.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000 + 3499) } // within 3500
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_FailsWhenDurationOutsideMargin()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "song.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000 + 3501) } // outside
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.Null(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_IgnoresDurationIfMissing()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "song.flac",
            Tracks = new List<TrackResource>() // no duration
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_ResolvesMultipleToTargetRelease()
    {
        var match1 = new MappingCandidate("option_0", 10, "label1", 1, 1, 100, "Song", 1, 180000, false);
        var match2 = new MappingCandidate("option_1", 20, "label2", 1, 1, 200, "Song", 1, 180000, false); // same title
        var row = new ManualImportResource
        {
            Name = "song.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) },
            AlbumReleaseId = 10
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { match1, match2 });
        Assert.NotNull(result);
        Assert.Equal(10, result.AlbumReleaseId);
    }

    [Fact]
    public void TryResolveDeterministicMatch_ReturnsNullIfMultipleNotOnTarget()
    {
        var match1 = new MappingCandidate("option_0", 10, "label1", 1, 1, 100, "Song", 1, 180000, false);
        var match2 = new MappingCandidate("option_1", 20, "label2", 1, 1, 200, "Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "song.flac",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) },
            AlbumReleaseId = 5 // neither match
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { match1, match2 });
        Assert.Null(result);
    }

    [Fact]
    public void TryResolveDeterministicMatch_StripsAudioExtensions()
    {
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 1, 100, "Song", 1, 180000, false);
        var row = new ManualImportResource
        {
            Name = "song.MP3",
            Tracks = new List<TrackResource> { Track(1, "Downloaded", 1, 180000) }
        };
        var result = _engine.TryResolveDeterministicMatch(row, new[] { candidate });
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildQuestions_ReturnsMappingAndReject()
    {
        var queueItem = new QueueResource { Title = "Download Title" };
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Name = "file.flac",
            FolderName = "Folder",
            Tracks = new List<TrackResource> { Track(1, "Song", 1, 180000) }
        };
        var candidate = new MappingCandidate("option_0", 10, "title: 'Song' | track: 1", 1, 1, 100, "Song", 1, 180000, false);
        var questions = _engine.BuildQuestions(queueItem, row, new[] { candidate });
        Assert.Contains("mapping", questions.Keys);
        Assert.Contains("reject", questions.Keys);
        Assert.Equal("choice", questions["mapping"].Type);
        Assert.Equal("noul", questions["reject"].Type);
        Assert.NotNull(questions["mapping"].Criteria);
        Assert.NotNull(questions["reject"].Criteria);
        Assert.Contains("option_0", questions["mapping"].Criteria!.Keys);
        Assert.Contains("no_match", questions["mapping"].Criteria!.Keys);
        Assert.Contains("false", questions["reject"].Criteria!.Keys);
        Assert.Contains("true", questions["reject"].Criteria!.Keys);
    }

    [Fact]
    public void BuildQuestions_HandlesOffTargetReleases()
    {
        var queueItem = new QueueResource { Title = "Download" };
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Name = "file.flac"
        };
        var candidate = new MappingCandidate("option_0", 20, "title: 'Song'", 1, 1, 100, "Song", 1, 180000, false);
        var questions = _engine.BuildQuestions(queueItem, row, new[] { candidate });
        Assert.Contains("option_0", questions["mapping"].Criteria!.Keys);
    }

    [Fact]
    public void BuildQuestions_HandlesHasFile()
    {
        var queueItem = new QueueResource { Title = "Download" };
        var row = new ManualImportResource
        {
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 1, Title = "Album" },
            AlbumReleaseId = 10,
            Name = "file.flac"
        };
        var candidate = new MappingCandidate("option_0", 10, "title: 'Song'", 1, 1, 100, "Song", 1, 180000, true);
        var questions = _engine.BuildQuestions(queueItem, row, new[] { candidate });
        var desc = questions["mapping"].Criteria!["option_0"];
        Assert.Contains("(already in library)", desc);
    }

    [Fact]
    public void BuildModelState_BuildsCorrectStructure()
    {
        var queueItem = new QueueResource { Title = "QueueTitle", Status = "Paused" };
        var row = new ManualImportResource
        {
            Name = "file.flac",
            RelativePath = "path/file.flac",
            Size = 12345,
            Artist = new ArtistResource { Id = 1, ArtistName = "Artist" },
            Album = new AlbumResource { Id = 2, Title = "Album" },
            AlbumReleaseId = 10,
            Quality = new QualityModel { Quality = new Quality { Name = "FLAC", Resolution = 0 } },
            Rejections = new List<Rejection> { new Rejection { Type = "unparseable" } },
            Tracks = new List<TrackResource> { Track(100, "Song", 1, 180000) }
        };
        var candidate = new MappingCandidate("option_0", 10, "label", 1, 2, 100, "Song", 1, 180000, false);
        var state = _engine.BuildModelState(queueItem, row, new[] { candidate });
        Assert.IsType<Dictionary<string, object?>>(state);
        var dict = (Dictionary<string, object?>)state;
        Assert.Equal("file.flac", dict["file_name"]);
        Assert.Equal("path/file.flac", dict["relative_path"]);
        Assert.Equal(12345L, dict["size"]);
        Assert.Equal("Artist", dict["artist"]);
        Assert.Equal("Album", dict["album"]);
        Assert.Equal(10, dict["album_release_id"]);
        Assert.NotNull(dict["quality"]);
        Assert.NotNull(dict["rejections"]);
        Assert.NotNull(dict["tracks"]);
        Assert.NotNull(dict["candidates"]);
        Assert.Equal("QueueTitle", dict["download_title"]);
        Assert.Equal("Paused", dict["queue_status"]);
    }

    [Fact]
    public void ToImportUpdate_CreatesUpdateWithCandidate()
    {
        var row = new ManualImportResource
        {
            Artist = new ArrImportSolver.Lidarr.Models.ArtistResource { Id = 1 },
            Album = new ArrImportSolver.Lidarr.Models.AlbumResource { Id = 2 },
            AlbumReleaseId = 10,
            TrackIds = new List<int> { 5, 6 }
        };
        var candidate = new MappingCandidate("option_0", 10, "label", 5, 6, 100, "Song", 1, 180000, false);
        var update = _engine.ToImportUpdate(row, candidate);
        Assert.NotNull(update);
        Assert.Single(update.TrackIds);
        Assert.Contains(100, update.TrackIds);
        Assert.Equal(5, update.ArtistId);
        Assert.Equal(6, update.AlbumId);
        Assert.Equal(10, update.AlbumReleaseId);
        Assert.False(update.DisableReleaseSwitching);
    }

    [Fact]
    public void ToImportUpdate_FallsBackToRowValues_WhenNoCandidate()
    {
        var row = new ManualImportResource
        {
            Artist = new ArrImportSolver.Lidarr.Models.ArtistResource { Id = 1 },
            Album = new ArrImportSolver.Lidarr.Models.AlbumResource { Id = 2 },
            AlbumReleaseId = 10,
            TrackIds = new List<int> { 5, 6 }
        };
        var update = _engine.ToImportUpdate(row, null);
        Assert.Equal(2, update.TrackIds.Count);
        Assert.Contains(5, update.TrackIds);
        Assert.Contains(6, update.TrackIds);
    }

    [Fact]
    public void ToImportUpdate_UsesTracksWhenTrackIdsEmpty()
    {
        var row = new ManualImportResource
        {
            Artist = new ArrImportSolver.Lidarr.Models.ArtistResource { Id = 1 },
            Album = new ArrImportSolver.Lidarr.Models.AlbumResource { Id = 2 },
            AlbumReleaseId = 10,
            TrackIds = new List<int>(),
            Tracks = new List<TrackResource> { Track(7, "Song", 1, 180000) }
        };
        var update = _engine.ToImportUpdate(row, null);
        Assert.Single(update.TrackIds);
        Assert.Contains(7, update.TrackIds);
    }

}
