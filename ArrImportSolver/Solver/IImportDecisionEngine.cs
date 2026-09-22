using ArrImportSolver.Lidarr.Models;

namespace ArrImportSolver.Solver;

public enum RowKind
{
    AdditionalFile,
    Resolved,
    Ambiguous
}

public sealed record RowClassification(ManualImportResource Row, RowKind Kind);

public sealed record MappingCandidate(
    string Key,
    int AlbumReleaseId,
    string Label,
    int ArtistId,
    int AlbumId,
    int TrackId = 0,
    string? TrackTitle = null,
    int? TrackNumber = null,
    int TrackDurationMs = 0,
    bool HasFile = false);

public interface IImportDecisionEngine
{
    IReadOnlyList<RowClassification> Classify(IReadOnlyList<ManualImportResource> rows);

    /// <summary>
    /// Deterministic exact-match fast path, evaluated before any model call: returns the single
    /// candidate whose title tokens appear contiguously (normalised) in the downloaded file's name
    /// and whose track length is within a low margin of the downloaded file's length (when known),
    /// ignoring track number. Returns null when nothing matches, or when several candidates match
    /// (genuinely ambiguous — defer to the model).
    /// </summary>
    MappingCandidate? TryResolveDeterministicMatch(ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates);

    Task<IReadOnlyList<MappingCandidate>> BuildCandidatesAsync(ManualImportResource row,
        IReadOnlyDictionary<int, IReadOnlyList<TrackResource>> releaseTracks, CancellationToken ct);

    Dictionary<string, LayaQuestion> BuildQuestions(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates);

    object BuildModelState(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates);

    ManualImportFile ToImportUpdate(ManualImportResource row, MappingCandidate? candidate);
}