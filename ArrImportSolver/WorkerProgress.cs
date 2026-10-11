namespace ArrImportSolver;

/// <summary>
/// Live view of what the worker is doing right now, so the UI can show the entry currently under
/// evaluation and how much of the queue is left. The snapshot is replaced atomically on every
/// update, so a reader always observes a self-consistent set of values.
/// </summary>
public sealed class WorkerProgress
{
    private volatile WorkerProgressSnapshot _current = WorkerProgressSnapshot.Idle;

    public WorkerProgressSnapshot Current => _current;

    /// <summary>Marks the start of a poll pass over <paramref name="queueCount"/> queue entries.</summary>
    public void BeginCycle(int queueCount)
    {
        _current = new WorkerProgressSnapshot
        {
            QueueCount = Math.Max(0, queueCount),
            ProcessedCount = 0,
            CurrentItem = null,
            CurrentDownloadId = null,
            CurrentFile = null,
            InProgress = true,
            CycleStartedUtc = DateTime.UtcNow
        };
    }

    /// <summary>Marks the item at <paramref name="index"/> (zero-based) as the one being evaluated.</summary>
    public void BeginItem(int index, string? title, string? downloadId)
    {
        var previous = _current;
        _current = new WorkerProgressSnapshot
        {
            QueueCount = previous.QueueCount,
            // Items before this one are done, which makes the count progress one entry at a time.
            ProcessedCount = Math.Clamp(index, 0, previous.QueueCount),
            CurrentItem = Blank(title),
            CurrentDownloadId = Blank(downloadId),
            CurrentFile = null,
            InProgress = true,
            CycleStartedUtc = previous.CycleStartedUtc
        };
    }

    /// <summary>Notes which file of the current entry is being evaluated.</summary>
    public void SetCurrentFile(string? fileName)
    {
        var previous = _current;
        _current = new WorkerProgressSnapshot
        {
            QueueCount = previous.QueueCount,
            ProcessedCount = previous.ProcessedCount,
            CurrentItem = previous.CurrentItem,
            CurrentDownloadId = previous.CurrentDownloadId,
            CurrentFile = Blank(fileName),
            InProgress = true,
            CycleStartedUtc = previous.CycleStartedUtc
        };
    }

    /// <summary>Marks the cycle as finished; the totals are kept so the UI can display them.</summary>
    public void EndCycle()
    {
        var previous = _current;
        _current = new WorkerProgressSnapshot
        {
            QueueCount = previous.QueueCount,
            ProcessedCount = previous.QueueCount,
            CurrentItem = null,
            CurrentDownloadId = null,
            CurrentFile = null,
            InProgress = false,
            CycleStartedUtc = previous.CycleStartedUtc
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class WorkerProgressSnapshot
{
    public static readonly WorkerProgressSnapshot Idle = new();

    /// <summary>Number of queue entries seen in the current (or last) cycle.</summary>
    public int QueueCount { get; init; }

    /// <summary>How many of them have been finished so far.</summary>
    public int ProcessedCount { get; init; }

    /// <summary>Title of the entry being evaluated right now, or null when idle.</summary>
    public string? CurrentItem { get; init; }

    public string? CurrentDownloadId { get; init; }

    /// <summary>File of the current entry being evaluated, when known.</summary>
    public string? CurrentFile { get; init; }

    public bool InProgress { get; init; }

    public DateTime? CycleStartedUtc { get; init; }
}