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

    // THE DETERMINISTIC GUARD (deferred): see PLAN notes. Laya's prompt has been sharpened
    // instead - field-labelled candidates, ASCII-only text, and an explicit instruction that
    // track title + duration are the primary match signals.
    //
    // /// <summary>
    // /// Deterministic cross-check: finds the single candidate whose title (normalised) matches the
    // /// downloaded track and whose track number and/or duration agree. Laya is confidently but
    // /// occasionally wrong (e.g. picking 'Papers' for a file that is exactly 'Hot Tottie'), so a
    // /// strong deterministic match outranks the model. Returns null when ambiguous.
    // /// </summary>
    // MappingCandidate? TryResolveDeterministicMatch(ManualImportResource row,
    //     IReadOnlyList<MappingCandidate> candidates);
    //
    // /// <summary>
    // /// Unique candidate whose normalised title matches the parsed track's title, ignoring track
    // /// number/duration. Used as a veto: if Laya confidently picks a different candidate while an
    // /// exact title match exists, that is the 'Papers instead of Hot Tottie' hallucination and we
    // /// refuse to act. Returns null when the title matches several candidates (genuinely ambiguous).
    // /// </summary>
    // MappingCandidate? TryFindTitleMatch(ManualImportResource row, IReadOnlyList<MappingCandidate> candidates);

    Task<IReadOnlyList<MappingCandidate>> BuildCandidatesAsync(ManualImportResource row,
        IReadOnlyDictionary<int, IReadOnlyList<TrackResource>> releaseTracks, CancellationToken ct);

    Dictionary<string, LayaQuestion> BuildQuestions(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates);

    object BuildModelState(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates);

    ManualImportUpdateResource ToImportUpdate(ManualImportResource row, MappingCandidate? candidate);
}