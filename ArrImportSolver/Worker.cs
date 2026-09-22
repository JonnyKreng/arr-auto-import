using System.Diagnostics;
using System.Text.Json;
using ArrImportSolver.Lidarr;
using ArrImportSolver.Lidarr.Models;
using ArrImportSolver.Options;
using ArrImportSolver.Solver;
using ArrImportSolver.Store;
using Microsoft.Extensions.Options;

namespace ArrImportSolver;

public class Worker(
    LidarrClient lidarr,
    LayaDecisionClient laya,
    IImportDecisionEngine engine,
    DecisionRepository store,
    IOptionsMonitor<LidarrOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string NoCandidatesMarker = "blocklisted release (no candidates)";

    private readonly Dictionary<int, IReadOnlyList<TrackResource>> _releaseTracks = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not request decisions until the sidecar has finished loading its model.
        if (await laya.WaitUntilReadyAsync(stoppingToken))
        {
            logger.LogInformation("Decision sidecar is ready at {Sidecar}", options.CurrentValue.SidecarUrl);
        }
        else
        {
            logger.LogError(
                "Decision sidecar never became ready at {Sidecar}; decisions will be left to a human",
                options.CurrentValue.SidecarUrl);
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.PollIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation(
            "ArrImportSolver started (poll {Interval}s, DryRun={DryRun}, MinConfidence={MinConfidence}, sidecar={Sidecar})",
            interval.TotalSeconds, options.CurrentValue.DryRun, options.CurrentValue.Model.MinConfidence,
            options.CurrentValue.SidecarUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(stoppingToken);

            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Poll cycle failed");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        _releaseTracks.Clear();
        var queue = await lidarr.GetQueueAsync(ct);

        foreach (var item in queue)
        {
            if (string.IsNullOrEmpty(item.DownloadId))
            {
                continue;
            }

            if (store.IsDownloadDone(item.DownloadId))
            {
                logger.LogDebug("Download {DownloadId} already marked done; skipping", item.DownloadId);
                continue;
            }

            try
            {
                await ProcessQueueItemAsync(item, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process queue item {QueueId} ({DownloadId})", item.Id, item.DownloadId);
            }
        }
    }

    private async Task ProcessQueueItemAsync(QueueResource item, CancellationToken ct)
    {
        var rows = await lidarr.GetManualImportAsync(item.DownloadId!, ct);
        if (rows.Count == 0)
        {
            return;
        }

        var sawImportAction = false;
        var pendingRejects = new List<DecisionRecord>();
        var pendingNoCandidate = new List<DecisionRecord>();
        var importFiles = new List<ManualImportFile>();
        var importRecords = new List<DecisionRecord>();

        foreach (var classification in engine.Classify(rows))
        {
            var row = classification.Row;

            switch (classification.Kind)
            {
                case RowKind.AdditionalFile:
                    var skipped = NewRecord(item, row, DecisionResolver.Deterministic);
                    skipped.Action = DecisionAction.Skip;
                    skipped.Status = DecisionStatus.Applied;
                    skipped.Chosen = "additional file (covers/nfo)";
                    store.Save(skipped);
                    logger.LogInformation("Skip additional file {Name}", row.Name);
                    break;

                case RowKind.Resolved:
                    sawImportAction = true;
                    var resolved = NewRecord(item, row, DecisionResolver.Deterministic);
                    resolved.Action = DecisionAction.Import;
                    resolved.Chosen = row.Album?.Title;
                    ApplyImport(resolved, row, candidate: null, importFiles, importRecords);
                    break;

                default:
                    var outcome = await ResolveWithLayaAsync(item, row, pendingRejects, pendingNoCandidate,
                        importFiles, importRecords, ct);
                    sawImportAction |= outcome == LayaOutcome.Import;
                    break;
            }
        }

        if (importFiles.Count > 0)
        {
            await SubmitImportsAsync(importFiles, importRecords, item, ct);
        }

        if (pendingNoCandidate.Count > 0 && pendingRejects.Count == 0)
        {
            await ApplyRejectsAsync(pendingNoCandidate, item, ct);
        }
        else if (pendingRejects.Count > 0)
        {
            if (sawImportAction)
            {
                if (pendingNoCandidate.Count > 0)
                {
                    await ApplyRejectsAsync(pendingNoCandidate, item, ct);
                }

                foreach (var record in pendingRejects)
                {
                    var existing = store.FindLatest(item.DownloadId!, record.RowId);
                    if (existing is not null && existing.Error is not null && existing.Error.Contains("superseded"))
                    {
                        logger.LogInformation("Already superseded {Name} earlier; skipping", record.RowId);
                        continue;
                    }

                    record.Action = DecisionAction.LeaveToHuman;
                    record.Status = DecisionStatus.SkippedDryRun;
                    record.Error = "superseded: another row of this download was imported this cycle";
                    store.Save(record);
                }
            }
            else
            {
                await ApplyRejectsAsync(pendingNoCandidate.Concat(pendingRejects).ToList(), item, ct);
            }
        }
    }

    private async Task<LayaOutcome> ResolveWithLayaAsync(QueueResource item, ManualImportResource row,
        List<DecisionRecord> pendingRejects, List<DecisionRecord> pendingNoCandidate,
        List<ManualImportFile> importFiles, List<DecisionRecord> importRecords, CancellationToken ct)
    {
        var candidates = await BuildCandidatesAsync(row, ct);
        if (candidates.Count == 0)
        {
            var existing = store.FindLatest(item.DownloadId!, row.Id.ToString());
            if (existing is not null &&
                existing.Chosen == NoCandidatesMarker &&
                existing.Status is DecisionStatus.Applied or DecisionStatus.SkippedDryRun)
            {
                logger.LogInformation("Already blocklisted {Name}; skipping re-decision", row.Name);
                return LayaOutcome.Skip;
            }

            var blockRecord = NewRecord(item, row, DecisionResolver.Deterministic);
            blockRecord.State = JsonSerializer.Serialize(engine.BuildModelState(item, row, candidates), JsonOptions);
            blockRecord.Candidates = "[]";
            blockRecord.Questions = null;
            blockRecord.Action = DecisionAction.RejectBlock;
            blockRecord.Chosen = NoCandidatesMarker;
            blockRecord.Status = DecisionStatus.Pending;
            pendingNoCandidate.Add(blockRecord);
            logger.LogInformation("No candidates for {Name} — marks for reject+blocklist", row.Name);
            return LayaOutcome.Reject;
        }

        var deterministic = engine.TryResolveDeterministicMatch(row, candidates);
        if (deterministic is not null)
        {
            var exactRecord = NewRecord(item, row, DecisionResolver.Deterministic);
            exactRecord.Candidates = JsonSerializer.Serialize(candidates, JsonOptions);
            exactRecord.State = JsonSerializer.Serialize(engine.BuildModelState(item, row, candidates), JsonOptions);
            exactRecord.Confidence = 1d;

            if (deterministic.HasFile)
            {
                exactRecord.Action = DecisionAction.RejectBlock;
                exactRecord.Chosen = $"duplicate: '{deterministic.TrackTitle}' already has a file";
                exactRecord.Status = DecisionStatus.Pending;
                pendingRejects.Add(exactRecord);
                logger.LogInformation(
                    "Deterministic match '{Track}' already has a file — rejecting+blocklisting download ({Name})",
                    deterministic.TrackTitle, row.Name);
                return LayaOutcome.Reject;
            }

            exactRecord.Action = DecisionAction.Import;
            exactRecord.Chosen = deterministic.Label;
            ApplyImport(exactRecord, row, deterministic, importFiles, importRecords);
            logger.LogInformation("Deterministic exact match: {Name} -> {Track}", row.Name, deterministic.Label);
            return LayaOutcome.Import;
        }

        var state = engine.BuildModelState(item, row, candidates);
        var questions = engine.BuildQuestions(item, row, candidates);

        var record = NewRecord(item, row, DecisionResolver.Laya);
        record.State = JsonSerializer.Serialize(state, JsonOptions);
        record.Candidates = JsonSerializer.Serialize(candidates, JsonOptions);
        record.Questions = JsonSerializer.Serialize(questions, JsonOptions);
        record.InputSize = record.State.Length;

        var request = new LayaDecideRequest { State = state, Questions = questions };

        var stopwatch = Stopwatch.StartNew();
        var response = await laya.DecideAsync(request, ct);
        stopwatch.Stop();
        record.LatencyMs = stopwatch.ElapsedMilliseconds;

        if (response is null || response.Error is not null)
        {
            record.Action = DecisionAction.LeaveToHuman;
            record.Status = DecisionStatus.Failed;
            record.Error = response?.Error ?? "sidecar returned no response";
            record.ModelAnswer = null;
            store.Save(record);
            logger.LogWarning("Decision sidecar unavailable for {Name}: {Error}", row.Name, record.Error);
            return LayaOutcome.LeaveToHuman;
        }

        record.ModelAnswer = JsonSerializer.Serialize(response, JsonOptions);

        var threshold = options.CurrentValue.Model.MinConfidence;

        // Question 1: is this the album we want to import? A confident "different release" gates
        // everything else - the download is blocklisted rather than force-mapped into the target album.
        if (response.Answers.TryGetValue("reject", out var reject))
        {
            var differentRelease = (reject.Noul ?? 0d) >= 0.5d;
            if (differentRelease && reject.Confidence >= threshold)
            {
                record.Action = DecisionAction.RejectBlock;
                record.Chosen = "blocklisted: not the target album";
                record.Confidence = reject.Confidence;
                record.Status = DecisionStatus.Pending;
                pendingRejects.Add(record);
                logger.LogInformation(
                    "Marks for reject+blocklist: {Name} is not the target album (confidence {Confidence:0.00})",
                    row.Name, reject.Confidence);
                return LayaOutcome.Reject;
            }
        }

        // Question 2: which track of the target album is this file?
        if (response.Answers.TryGetValue("mapping", out var mapping) && mapping.Choice is not null)
        {
            var chosen = candidates.FirstOrDefault(c => c.Key == mapping.Choice);
            if (chosen is not null && mapping.Confidence >= threshold)
            {
                if (chosen.HasFile)
                {
                    record.Action = DecisionAction.RejectBlock;
                    record.Chosen = $"duplicate: '{chosen.TrackTitle}' already has a file";
                    record.Confidence = mapping.Confidence;
                    record.Status = DecisionStatus.Pending;
                    pendingRejects.Add(record);
                    logger.LogInformation(
                        "Selected track '{Track}' already has a file — rejecting+blocklisting download ({Name}, confidence {Confidence:0.00})",
                        chosen.TrackTitle, row.Name, mapping.Confidence);
                    return LayaOutcome.Reject;
                }

                record.Action = DecisionAction.Import;
                record.Chosen = chosen.Label;
                record.Confidence = mapping.Confidence;
                ApplyImport(record, row, chosen, importFiles, importRecords);
                return LayaOutcome.Import;
            }

            record.Chosen = mapping.Choice == "no_match" ? "no_match" : $"unconfident ({mapping.Choice})";
            record.Confidence = mapping.Confidence;
        }

        record.Action = DecisionAction.LeaveToHuman;
        record.Status = DecisionStatus.Pending;
        record.Chosen ??= "low confidence / no action";
        record.Confidence ??= response.Answers.TryGetValue("reject", out var rej) ? rej.Confidence : null;
        store.Save(record);
        logger.LogInformation("Leave to human: {Name} (mapping={MappingChoice}, reject={Noul})",
            row.Name, response.Answers.TryGetValue("mapping", out var m) ? m.Choice : null,
            response.Answers.TryGetValue("reject", out var r) ? r.Noul : null);
        return LayaOutcome.LeaveToHuman;
    }

    private void ApplyImport(DecisionRecord record, ManualImportResource row, MappingCandidate? candidate,
        List<ManualImportFile> importFiles, List<DecisionRecord> importRecords)
    {
        var file = engine.ToImportUpdate(row, candidate);

        if (options.CurrentValue.DryRun)
        {
            record.Status = DecisionStatus.SkippedDryRun;
            logger.LogInformation(
                "[DryRun] would import {Name} -> {Album} (artist {ArtistId}, album {AlbumId}, release {ReleaseId})",
                row.Name, record.Chosen, file.ArtistId, file.AlbumId, file.AlbumReleaseId);
        }
        else
        {
            record.Status = DecisionStatus.Pending;
            importFiles.Add(file);
            importRecords.Add(record);
            logger.LogInformation("Will import {Name} -> {Album} (release {ReleaseId})", row.Name, record.Chosen,
                file.AlbumReleaseId);
        }

        store.Save(record);
    }

    private async Task SubmitImportsAsync(IReadOnlyList<ManualImportFile> importFiles,
        IReadOnlyList<DecisionRecord> importRecords, QueueResource item, CancellationToken ct)
    {
        try
        {
            await lidarr.ImportAsync(importFiles, ct);
            foreach (var record in importRecords)
            {
                record.Status = DecisionStatus.Done;
                store.Save(record);
            }

            store.MarkDownloadDone(item.DownloadId!);
            logger.LogInformation("Imported {Count} file(s) for download {DownloadId}; marked done",
                importFiles.Count, item.DownloadId);
        }
        catch (Exception ex)
        {
            foreach (var record in importRecords)
            {
                record.Status = DecisionStatus.Failed;
                record.Error = ex.Message;
                store.Save(record);
            }

            logger.LogError(ex, "Import command failed for download {DownloadId} ({Count} file(s))", item.DownloadId,
                importFiles.Count);
        }
    }

    private async Task ApplyRejectsAsync(IReadOnlyList<DecisionRecord> records, QueueResource item,
        CancellationToken ct)
    {
        if (options.CurrentValue.DryRun)
        {
            foreach (var record in records)
            {
                record.Status = DecisionStatus.SkippedDryRun;
                store.Save(record);
            }

            logger.LogInformation("[DryRun] would reject+blocklist queue item {QueueId} ({DownloadId})", item.Id,
                item.DownloadId);
            return;
        }

        try
        {
            await lidarr.DeleteQueueItemAsync(item.Id, removeFromClient: true, blocklist: true, ct);
            foreach (var record in records)
            {
                record.Status = DecisionStatus.Applied;
                store.Save(record);
            }

            logger.LogInformation("Rejected+blocklisted queue item {QueueId} ({DownloadId})", item.Id, item.DownloadId);
        }
        catch (Exception ex)
        {
            foreach (var record in records)
            {
                record.Status = DecisionStatus.Failed;
                record.Error = ex.Message;
                store.Save(record);
            }

            logger.LogError(ex, "Reject+blocklist failed for queue item {QueueId}", item.Id);
        }
    }

    private async Task<IReadOnlyList<MappingCandidate>> BuildCandidatesAsync(ManualImportResource row,
        CancellationToken ct)
    {
        var releases = row.AlbumReleases is { Count: > 0 }
            ? row.AlbumReleases
            : row.Album?.Releases;
        if (releases is { Count: > 0 })
        {
            foreach (var release in releases)
            {
                if (!release.Id.HasValue || _releaseTracks.ContainsKey(release.Id.Value))
                {
                    continue;
                }

                var tracks = await lidarr.GetReleaseTracksAsync(release.Id.Value, ct);
                _releaseTracks[release.Id.Value] = tracks;
            }
        }

        return await engine.BuildCandidatesAsync(row, _releaseTracks, ct);
    }

    private static DecisionRecord NewRecord(QueueResource item, ManualImportResource row, string resolver)
    {
        return new DecisionRecord
        {
            App = "Lidarr",
            DownloadId = item.DownloadId,
            QueueId = item.Id,
            FilePath = row.Path,
            FileName = row.Name,
            RowId = row.Id.ToString(),
            Resolver = resolver
        };
    }

    private enum LayaOutcome
    {
        Import,
        Reject,
        LeaveToHuman,
        Skip
    }
}