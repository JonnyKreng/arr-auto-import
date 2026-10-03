using ArrImportSolver.Store;

namespace ArrImportSolver.Tests;

public class DecisionRepositoryTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _origDir;

    public DecisionRepositoryTests()
    {
        _origDir = Directory.GetCurrentDirectory();
        _testDir = Path.Combine(Path.GetTempPath(), "arrsolver_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        Directory.SetCurrentDirectory(_testDir);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_origDir);
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void Save_And_GetRecent()
    {
        var repo = new DecisionRepository();
        var rec = new DecisionRecord
        {
            DownloadId = "DL1",
            RowId = "row1",
            Action = "Import",
            Status = "Pending"
        };
        repo.Save(rec);
        Assert.True(rec.Id > 0);
        var recent = repo.GetRecent(10);
        Assert.Single(recent);
        Assert.Equal("DL1", recent[0].DownloadId);
    }

    [Fact]
    public void FindLatest_ByDownloadAndRow()
    {
        var repo = new DecisionRepository();
        var rec1 = new DecisionRecord { DownloadId = "DL1", RowId = "row1", Action = "Import" };
        repo.Save(rec1);
        var rec2 = new DecisionRecord { DownloadId = "DL1", RowId = "row1", Action = "Reject" };
        repo.Save(rec2);
        var latest = repo.FindLatest("DL1", "row1");
        Assert.NotNull(latest);
        Assert.Equal("Reject", latest.Action);
        Assert.Equal(rec2.Id, latest.Id);
    }

    [Fact]
    public void FindLatestByDownload()
    {
        var repo = new DecisionRepository();
        repo.Save(new DecisionRecord { DownloadId = "DL1", Action = "A" });
        repo.Save(new DecisionRecord { DownloadId = "DL1", Action = "B" });
        var latest = repo.FindLatestByDownload("DL1");
        Assert.NotNull(latest);
        Assert.Equal("B", latest.Action);
    }

    [Fact]
    public void MarkDownloadDone_And_IsDownloadDone()
    {
        var repo = new DecisionRepository();
        Assert.False(repo.IsDownloadDone("DL1"));
        repo.MarkDownloadDone("DL1");
        Assert.True(repo.IsDownloadDone("DL1"));
    }

    [Fact]
    public void RestartAlbum_ResetsDoneAndRestartsRecords()
    {
        var repo = new DecisionRepository();
        repo.MarkDownloadDone("DL1");
        repo.Save(new DecisionRecord { DownloadId = "DL1", Status = "Applied" });
        repo.Save(new DecisionRecord { DownloadId = "DL1", Status = "Applied" });
        var hadRestart = repo.RestartAlbum("DL1");
        Assert.True(hadRestart);
        Assert.False(repo.IsDownloadDone("DL1"));
        var recent = repo.GetRecent();
        Assert.All(recent, r => Assert.Equal("Restarted", r.Status));
    }

    [Fact]
    public void RestartAlbum_ReturnsTrueIfNoDoneButHasRecords()
    {
        var repo = new DecisionRepository();
        repo.Save(new DecisionRecord { DownloadId = "DL1", Status = "Applied" });
        var hadRestart = repo.RestartAlbum("DL1");
        Assert.True(hadRestart);
        Assert.False(repo.IsDownloadDone("DL1"));
    }

    [Fact]
    public void RestartAlbum_ReturnsFalseIfNothingExists()
    {
        var repo = new DecisionRepository();
        var hadRestart = repo.RestartAlbum("DL1");
        Assert.False(hadRestart);
    }

    [Fact]
    public void GetRecent_ClampsNegativeToAll()
    {
        var repo = new DecisionRepository();
        for (int i = 0; i < 3; i++) repo.Save(new DecisionRecord { DownloadId = $"DL{i}" });
        var recent = repo.GetRecent(-1);
        Assert.Equal(3, recent.Count);
    }

    [Fact]
    public void GetRecent_ClampsLargeTo5000()
    {
        var repo = new DecisionRepository();
        for (int i = 0; i < 2; i++) repo.Save(new DecisionRecord { DownloadId = $"DL{i}" });
        var recent = repo.GetRecent(10000);
        Assert.Equal(2, recent.Count);
    }
}
