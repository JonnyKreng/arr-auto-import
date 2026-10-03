using System.Text.Json;
using ArrImportSolver.Lidarr.Models;

namespace ArrImportSolver.Tests;

public class LidarrModelsTests
{
    [Fact]
    public void ManualImportFile_SerializesWithCorrectFormat()
    {
        var file = new ManualImportFile
        {
            Path = "/path/to/file.flac",
            TrackIds = new List<int> { 1, 2, 3 },
            ArtistId = 10,
            AlbumId = 20,
            AlbumReleaseId = 30,
            DisableReleaseSwitching = false
        };
        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"path\":\"/path/to/file.flac\"", json);
        Assert.Contains("\"trackIds\":[1,2,3]", json);
        Assert.Contains("\"artistId\":10", json);
        Assert.Contains("\"albumId\":20", json);
        Assert.Contains("\"albumReleaseId\":30", json);
        Assert.Contains("\"disableReleaseSwitching\":false", json);
    }

    [Fact]
    public void ManualImportCommand_SerializesWithNullsIgnored()
    {
        var cmd = new ManualImportCommand
        {
            Name = "ManualImport",
            Files = new List<ManualImportFile>(),
            ImportMode = "auto",
            ReplaceExistingFiles = false
        };
        var json = JsonSerializer.Serialize(cmd, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        Assert.Contains("\"name\":\"ManualImport\"", json);
        Assert.Contains("\"importMode\":\"auto\"", json);
        Assert.Contains("\"replaceExistingFiles\":false", json);
    }
}
