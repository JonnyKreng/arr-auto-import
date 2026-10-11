namespace ArrImportSolver.Tests;

public class WorkerProgressTests
{
    [Fact]
    public void StartsIdle()
    {
        var progress = new WorkerProgress();
        var snapshot = progress.Current;
        Assert.False(snapshot.InProgress);
        Assert.Null(snapshot.CurrentItem);
        Assert.Equal(0, snapshot.QueueCount);
        Assert.Equal(0, snapshot.ProcessedCount);
    }

    [Fact]
    public void BeginCycle_RecordsQueueSize()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(3);
        var snapshot = progress.Current;
        Assert.True(snapshot.InProgress);
        Assert.Equal(3, snapshot.QueueCount);
        Assert.Equal(0, snapshot.ProcessedCount);
        Assert.NotNull(snapshot.CycleStartedUtc);
    }

    [Fact]
    public void BeginItem_ReportsItemAndProgress()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(3);
        progress.BeginItem(1, "Some Album", "abc");
        var snapshot = progress.Current;
        Assert.Equal("Some Album", snapshot.CurrentItem);
        Assert.Equal("abc", snapshot.CurrentDownloadId);
        Assert.Equal(1, snapshot.ProcessedCount);
        Assert.Equal(3, snapshot.QueueCount);
    }

    [Fact]
    public void SetCurrentFile_KeepsItemAndAddsFile()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(2);
        progress.BeginItem(0, "Album", "dl");
        progress.SetCurrentFile("01 - Song.flac");
        var snapshot = progress.Current;
        Assert.Equal("Album", snapshot.CurrentItem);
        Assert.Equal("01 - Song.flac", snapshot.CurrentFile);
    }

    [Fact]
    public void EndCycle_MarksAllProcessedAndClearsCurrent()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(2);
        progress.BeginItem(0, "Album", "dl");
        progress.SetCurrentFile("a.flac");
        progress.EndCycle();
        var snapshot = progress.Current;
        Assert.False(snapshot.InProgress);
        Assert.Null(snapshot.CurrentItem);
        Assert.Null(snapshot.CurrentFile);
        Assert.Equal(2, snapshot.ProcessedCount);
        Assert.Equal(2, snapshot.QueueCount);
    }

    [Fact]
    public void ProcessedCount_NeverExceedsQueueCount()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(2);
        progress.BeginItem(99, "Album", "dl");
        Assert.Equal(2, progress.Current.ProcessedCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankValues_BecomeNull(string? value)
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(1);
        progress.BeginItem(0, value, value);
        var snapshot = progress.Current;
        Assert.Null(snapshot.CurrentItem);
        Assert.Null(snapshot.CurrentDownloadId);
    }

    [Fact]
    public void NegativeQueueCount_IsClampedToZero()
    {
        var progress = new WorkerProgress();
        progress.BeginCycle(-5);
        Assert.Equal(0, progress.Current.QueueCount);
    }
}