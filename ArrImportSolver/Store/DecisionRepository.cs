using LiteDB;

namespace ArrImportSolver.Store;

public sealed class DecisionRecord
{
    [BsonId] public int Id { get; set; }

    public DateTime UtcTimestamp { get; set; }

    public string App { get; set; } = "Lidarr";

    public string? DownloadId { get; set; }

    public int? QueueId { get; set; }

    public string? FilePath { get; set; }

    public string? FileName { get; set; }

    public string? RowId { get; set; }

    public string? Candidates { get; set; }

    public string? Questions { get; set; }

    public string Resolver { get; set; } = "";

    public string? State { get; set; }

    public string? ModelAnswer { get; set; }

    public string Action { get; set; } = "";

    public string Status { get; set; } = "";

    public string? Error { get; set; }

    public long InputSize { get; set; }

    public long LatencyMs { get; set; }

    public double? Confidence { get; set; }

    public string? Chosen { get; set; }
}

public static class DecisionAction
{
    public const string Import = "Import";
    public const string RejectBlock = "RejectBlock";
    public const string Skip = "Skip";
    public const string LeaveToHuman = "LeaveToHuman";
}

public static class DecisionStatus
{
    public const string Pending = "Pending";
    public const string Applied = "Applied";
    public const string SkippedDryRun = "SkippedDryRun";
    public const string Failed = "Failed";
    public const string Done = "Done";
    public const string Restarted = "Restarted";
}

public sealed class DownloadDoneState
{
    [BsonId] public string DownloadId { get; set; } = "";

    public DateTime UtcTimestamp { get; set; }
}

public static class DecisionResolver
{
    public const string Deterministic = "Deterministic";
    public const string Laya = "Laya";
}

public class DecisionRepository
{
    private const string CollectionName = "decisions";
    private const string DoneCollectionName = "done";

    private readonly LiteDatabase _db;
    private readonly ILiteCollection<DecisionRecord> _decisions;
    private readonly ILiteCollection<DownloadDoneState> _doneDownloads;

    public DecisionRepository()
    {
        var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
        Directory.CreateDirectory(dataDir);

        _db = new LiteDatabase(new ConnectionString
        {
            Filename = Path.Combine(dataDir, "decisions.db"),
            Connection = ConnectionType.Shared
        });

        _decisions = _db.GetCollection<DecisionRecord>(CollectionName);
        _decisions.EnsureIndex(x => x.UtcTimestamp);

        _doneDownloads = _db.GetCollection<DownloadDoneState>(DoneCollectionName);
        _doneDownloads.EnsureIndex(x => x.UtcTimestamp);
    }

    public void Save(DecisionRecord record)
    {
        if (record.Id == 0)
        {
            record.UtcTimestamp = DateTime.UtcNow;
            _decisions.Insert(record);
        }
        else
        {
            _decisions.Update(record);
        }
    }

    public DecisionRecord? FindLatest(string downloadId, string rowId)
    {
        return _decisions
            .Query()
            .Where(x => x.DownloadId == downloadId && x.RowId == rowId)
            .OrderByDescending(x => x.UtcTimestamp)
            .FirstOrDefault();
    }

    public IReadOnlyList<DecisionRecord> GetRecent(int limit = 200)
    {
        var query = _decisions
            .Query()
            .OrderByDescending(x => x.UtcTimestamp);

        return limit > 0
            ? query.Limit(Math.Clamp(limit, 1, 5000)).ToArray()
            : query.ToArray();
    }

    public void MarkDownloadDone(string downloadId)
    {
        _doneDownloads.Upsert(new DownloadDoneState
        {
            DownloadId = downloadId,
            UtcTimestamp = DateTime.UtcNow
        });
    }

    public bool IsDownloadDone(string downloadId)
    {
        return _doneDownloads.FindById(downloadId) is not null;
    }

    // Clears the download-complete marker and flags every recorded decision for the
    // download as Restarted so the worker re-decides the album on the next poll (the
    // blocklist/re-decision guards in Worker look at the latest decision's status).
    public bool RestartAlbum(string downloadId)
    {
        var hadDone = _doneDownloads.Delete(downloadId);

        var records = _decisions
            .Query()
            .Where(x => x.DownloadId == downloadId)
            .ToArray();

        foreach (var record in records)
        {
            record.Status = DecisionStatus.Restarted;
            _decisions.Update(record);
        }

        return hadDone || records.Length > 0;
    }
}