using System.Text;
using System.Text.Json;
using ArrImportSolver.Lidarr.Models;

namespace ArrImportSolver.Solver;

public class ImportDecisionEngine : IImportDecisionEngine
{
    public IReadOnlyList<RowClassification> Classify(IReadOnlyList<ManualImportResource> rows)
    {
        var result = new List<RowClassification>(rows.Count);
        foreach (var row in rows)
        {
            var kind = ClassifyRow(row);
            result.Add(new RowClassification(row, kind));
        }

        return result;
    }

    private static RowKind ClassifyRow(ManualImportResource row)
    {
        if (row.AdditionalFile)
        {
            return RowKind.AdditionalFile;
        }

        bool rejectionsPresent = row.Rejections is { Count: > 0 };
        bool fullyParsed = row.Artist != null && row.Album != null && row.AlbumReleaseId.HasValue &&
                           row.Quality != null;

        return !rejectionsPresent && fullyParsed ? RowKind.Resolved : RowKind.Ambiguous;
    }

    public async Task<IReadOnlyList<MappingCandidate>> BuildCandidatesAsync(ManualImportResource row,
        IReadOnlyDictionary<int, IReadOnlyList<TrackResource>> releaseTracks, CancellationToken ct)
    {
        var list = new List<MappingCandidate>();
        var releases = row.AlbumReleases is { Count: > 0 }
            ? row.AlbumReleases
            : row.Album?.Releases;

        if (row.Artist == null || row.Album == null || releases is not { Count: > 0 })
        {
            return list;
        }

        var ordered = releases
            .Where(r => r.Id.HasValue)
            .OrderByDescending(r => r.Id == row.AlbumReleaseId)
            .ToList();

        var seen = new HashSet<string>();
        var index = 0;
        foreach (var release in ordered)
        {
            if (!releaseTracks.TryGetValue(release.Id!.Value, out var tracks))
            {
                continue;
            }

            foreach (var track in tracks)
            {
                if (!seen.Add(CandidateKey(track)))
                {
                    continue;
                }

                list.Add(new MappingCandidate(
                    $"option_{index}",
                    release.Id.Value,
                    FormatTrack(release, track, row.Album),
                    row.Artist.Id,
                    row.Album.Id,
                    track.Id,
                    track.Title,
                    track.TrackNumber.HasValue && TryTrackNumber(track.TrackNumber.Value, out var tn) ? tn : null,
                    track.Duration,
                    track.HasFile));
                index++;
            }
        }

        return list;
    }

    private static string CandidateKey(TrackResource track)
    {
        var title = (track.Title ?? string.Empty).Trim().ToLowerInvariant();
        return $"{title}|{TrackNumberText(track.TrackNumber)}|{track.Duration}";
    }

    private static bool TryTrackNumber(JsonElement el, out int number)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number when el.TryGetInt32(out var n):
                number = n;
                return true;
            case JsonValueKind.String when int.TryParse(el.GetString(), out var s):
                number = s;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    public Dictionary<string, LayaQuestion> BuildQuestions(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates)
    {
        var tracks = row.Tracks;
        var targetAlbum = TargetAlbumLabel(row);
        var downloadedRelease = DownloadedReleaseLabel(queueItem, row);
        var downloadedName = downloadedRelease is not null ? $"'{downloadedRelease}'" : "the download";

        // Only mark the target release when a candidate comes from a different one; when every
        // candidate is from the target release the marker is pure noise and eats the option-token
        // budget, truncating the track number and duration that identify each option.
        var hasOffTargetRelease = candidates.Any(c => c.AlbumReleaseId != row.AlbumReleaseId);

        var criteria = new Dictionary<string, string?>();
        foreach (var candidate in candidates)
        {
            var description = candidate.Label;
            if (hasOffTargetRelease && candidate.AlbumReleaseId == row.AlbumReleaseId)
            {
                description += " (target release)";
            }

            if (candidate.HasFile)
            {
                description += " (already in library)";
            }

            criteria[candidate.Key] = description;
        }

        criteria["no_match"] = "none of the listed tracks is the downloaded track";

        var trackSummary = tracks is { Count: > 0 }
            ? string.Join("; ", tracks.Select(FormatTrackSummary))
            : null;
        var downloadedInfo =
            $" The download archive name is {downloadedName}; the file name is '{ToAscii(row.Name)}'" +
            (trackSummary != null ? $" and contains: {trackSummary}." : ".");

        var mappingInstructions =
             $"""
             We want to import a downloaded file into the album {targetAlbum}. Which track of that album is the downloaded file?
             {downloadedInfo} Decide by TITLE and LENGTH first: the matching candidate 
             has the same title and a nearly equal length, even when its track number differs (deluxe edition, second 
             disc or a re-numbering). A radio edit or live version can be shorter or longer, so the title is the decisive 
             signal and the length confirms it. If no candidate shares the title and length, answer no_match.
             """;

//             $"""
//             We want to match a downloaded file into the album {targetAlbum}. Which track of that album is the downloaded file?
//             {downloadedInfo}
//             """;

        var rejectInstructions =
            $"""
             We want to import the album {targetAlbum}. Is the download a release other than {targetAlbum} - 
             a different album, a different artist, or garbage - and should therefore be rejected and blocklisted 
             so Lidarr does not grab it again? {downloadedInfo}
             """;

        if (candidates.Count > 0)
        {
            rejectInstructions += " Tracks on the album we want to import: " +
                string.Join("; ", candidates.Select(c => $"{c.Key}: {c.Label}")) + ".";
        }

        return new Dictionary<string, LayaQuestion>
        {
            ["mapping"] = new()
            {
                Type = "choice",
                Instructions = mappingInstructions,
                Criteria = criteria
            },
            ["reject"] = new()
            {
                Type = "noul",
                Instructions = rejectInstructions,
                Criteria = new Dictionary<string, string?>
                {
                    ["false"] =
                        $"No - the download is the album we want to import ({targetAlbum}); keep it and map its tracks.",
                    ["true"] =
                        $"Yes - the download is not {targetAlbum} but a different album, a different artist or garbage; reject and blocklist it."
                }
            }
        };
    }

    private static string? DownloadedReleaseLabel(QueueResource queueItem, ManualImportResource row)
    {
        foreach (var value in new[] { row.FolderName, queueItem.Title, row.RelativePath })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return ToAscii(value.Trim());
            }
        }

        return null;
    }

    private static string TargetAlbumLabel(ManualImportResource row)
    {
        var album = row.Album?.Title;
        var artist = row.Artist?.ArtistName;
        var hasAlbum = !string.IsNullOrWhiteSpace(album);
        var hasArtist = !string.IsNullOrWhiteSpace(artist);

        return (hasAlbum, hasArtist) switch
        {
            (true, true) => $"'{ToAscii(album)}' by '{ToAscii(artist)}'",
            (true, false) => $"'{ToAscii(album)}'",
            (false, true) => $"the album by '{ToAscii(artist)}'",
            _ => "the album we want to import"
        };
    }

    public object BuildModelState(QueueResource queueItem, ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates)
    {
        return new Dictionary<string, object?>
        {
            ["file_name"] = ToAscii(row.Name),
            ["relative_path"] = ToAscii(row.RelativePath),
            ["size"] = row.Size,
            ["artist"] = ToAscii(row.Artist?.ArtistName),
            ["album"] = ToAscii(row.Album?.Title),
            ["album_release_id"] = row.AlbumReleaseId,
            ["quality"] = ToAscii(row.Quality?.Quality.Name),
            ["rejections"] = row.Rejections?.Select(r => ToAscii(r.Reason)).ToList(),
            ["tracks"] = row.Tracks?.Select(t => new
            {
                title = ToAscii(t.Title),
                number = ToAscii(TrackNumberText(t.TrackNumber)),
                duration_ms = t.Duration,
                has_file = t.HasFile
            }).ToList(),
            ["candidates"] = candidates.Select(c => new
            {
                id = c.TrackId,
                release_id = c.AlbumReleaseId,
                track_title = ToAscii(c.TrackTitle),
                track_number = c.TrackNumber,
                duration_ms = c.TrackDurationMs,
                label = ToAscii(c.Label)
            }).ToList(),
            ["download_title"] = ToAscii(queueItem.Title),
            ["queue_status"] = ToAscii(queueItem.Status)
        };
    }

    public ManualImportFile ToImportUpdate(ManualImportResource row, MappingCandidate? candidate)
    {
        var trackIds = candidate?.TrackId is > 0
            ? [candidate.TrackId]
            : row.TrackIds is { Count: > 0 }
                ? row.TrackIds
                : row.Tracks is { Count: > 0 }
                    ? row.Tracks.Select(t => t.Id).ToList()
                    : new List<int>();

        return new ManualImportFile
        {
            Path = row.Path,
            ArtistId = candidate?.ArtistId ?? row.Artist?.Id ?? 0,
            AlbumId = candidate?.AlbumId ?? row.Album?.Id ?? 0,
            AlbumReleaseId = candidate?.AlbumReleaseId ?? row.AlbumReleaseId ?? 0,
            Quality = row.Quality,
            TrackIds = trackIds,
            DownloadId = row.DownloadId,
            DisableReleaseSwitching = false
        };
    }

    public MappingCandidate? TryResolveDeterministicMatch(ManualImportResource row,
        IReadOnlyList<MappingCandidate> candidates)
    {
        var fileName = row.Name;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var ext = Path.GetExtension(fileName);
        if (AudioExtensions.Contains(ext))
        {
            fileName = fileName[..^ext.Length];
        }

        var fileTokens = TitleTokens(fileName);
        if (fileTokens.Count == 0)
        {
            return null;
        }

        var downloadedDurationMs = DownloadedTrackDurationMs(row);
        var matches = new List<MappingCandidate>();
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.TrackTitle) &&
                EndsWithTokenSequence(fileTokens, TitleTokens(candidate.TrackTitle)) &&
                TrackLengthWithinMargin(candidate.TrackDurationMs, downloadedDurationMs))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        // Several releases share the same title. Resolve only when exactly one lives on the
        // target album release; every other collision is genuinely ambiguous and goes to the model.
        var onTarget = matches.Where(m => m.AlbumReleaseId == row.AlbumReleaseId).ToList();
        return onTarget.Count == 1 ? onTarget[0] : null;
    }

    // Durations in Lidarr are in milliseconds. A "low margin" keeps the deterministic path
    // conservative: title matches whose length differs by more than a few seconds are treated
    // as ambiguous (e.g. a live/radio re-recording) and deferred to the model.
    private const int TrackLengthMarginMs = 3500;

    private static int? DownloadedTrackDurationMs(ManualImportResource row)
    {
        if (row.Tracks is not { Count: > 0 })
        {
            return null;
        }

        foreach (var track in row.Tracks)
        {
            if (track.Duration > 0)
            {
                return track.Duration;
            }
        }

        return null;
    }

    private static bool TrackLengthWithinMargin(int candidateDurationMs, int? downloadedDurationMs)
    {
        if (downloadedDurationMs is null || candidateDurationMs <= 0 || downloadedDurationMs <= 0)
        {
            return true;
        }

        return Math.Abs(candidateDurationMs - downloadedDurationMs.Value) <= TrackLengthMarginMs;
    }

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".aiff", ".ape", ".flac", ".m4a", ".mp2", ".mp3", ".ogg", ".opus", ".wav", ".wma"
    };

    private static IReadOnlyList<string> TitleTokens(string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Drop apostrophes (and typographic variants) instead of splitting on them, so the
            // filename "dont_stop" and the title "Don't Stop" both normalize to the same tokens.
            if (ch is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                continue;
            }

            normalized.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool EndsWithTokenSequence(IReadOnlyList<string> tokens, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || tokens.Count < sequence.Count)
        {
            return false;
        }

        var start = tokens.Count - sequence.Count;
        for (var i = 0; i < sequence.Count; i++)
        {
            if (!string.Equals(tokens[start + i], sequence[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string TrackNumberText(JsonElement? el)
    {
        if (el is null)
        {
            return string.Empty;
        }

        var value = el.Value;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static string FormatTrack(AlbumReleaseResource release, TrackResource track, AlbumResource album)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            parts.Add($"title: '{ToAscii(track.Title)}'");
        }

        var tn = TrackNumberText(track.TrackNumber);
        if (!string.IsNullOrWhiteSpace(tn))
        {
            parts.Add($"track: {ToAscii(tn)}");
        }

        if (track.Duration > 0)
        {
            parts.Add($"length: {TimeSpan.FromMilliseconds(track.Duration):m\\:ss}");
        }

        if (!string.IsNullOrWhiteSpace(release.Title))
        {
            parts.Add($"release: '{ToAscii(release.Title)}'");
        }
        
        if (!string.IsNullOrWhiteSpace(release.ReleaseDate))
        {
            parts.Add($"released: {ToAscii(release.ReleaseDate)}");
        }

        if (parts.Count > 0)
        {
            return string.Join(" | ", parts);
        }

        return ToAscii(album.Title ?? release.ForeignReleaseId ?? release.Id?.ToString() ?? "unknown");
    }

    private static string FormatTrackSummary(TrackResource track)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Title))
        {
            parts.Add($"title: '{ToAscii(track.Title)}'");
        }

        var tn = TrackNumberText(track.TrackNumber);
        if (!string.IsNullOrWhiteSpace(tn))
        {
            parts.Add($"track: {ToAscii(tn)}");
        }

        if (track.Duration > 0)
        {
            parts.Add($"length: {TimeSpan.FromMilliseconds(track.Duration):m\\:ss}");
        }

        return parts.Count > 0 ? string.Join(" | ", parts) : ToAscii(track.Title ?? "unknown");
    }

    private static string ToAscii(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch < 128)
            {
                builder.Append(ch);
                continue;
            }

            // Normalize curly apostrophes / quotes and dashes to their ASCII neighbors; drop
            // everything else (accents, emoji, combining marks, ...). The sidecar model is only ever fed ASCII.
            builder.Append(ch switch
            {
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                '\u2013' or '\u2014' or '\u2015' => '-',
                _ => char.MinValue
            });
        }

        return builder.Replace("\0", "").ToString().Trim();
    }
}