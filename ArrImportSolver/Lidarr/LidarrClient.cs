using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArrImportSolver.Lidarr.Models;
using ArrImportSolver.Options;
using Microsoft.Extensions.Options;

namespace ArrImportSolver.Lidarr;

public class LidarrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<LidarrOptions> _options;
    private readonly ILogger<LidarrClient> _logger;

    public LidarrClient(HttpClient http, IOptionsMonitor<LidarrOptions> options, ILogger<LidarrClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
        _http.DefaultRequestHeaders.Add("X-Api-Key", options.CurrentValue.Key);
    }

    private Uri Base => new(_options.CurrentValue.Url);

    public async Task<IReadOnlyList<QueueResource>> GetQueueAsync(CancellationToken ct)
    {
        var url =
            "/api/v1/queue?page=1&pageSize=100&includeArtist=true&includeAlbum=true&sortKey=timeleft&sortDirection=ascending";
        try
        {
            var page = await _http.GetFromJsonAsync<PagingResource<QueueResource>>(new Uri(Base, url), JsonOptions, ct);
            return page?.Records ?? new List<QueueResource>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch queue from Lidarr at {Url}", Base);
            throw;
        }
    }

    public async Task<IReadOnlyList<TrackResource>> GetAlbumTracksAsync(int albumId, CancellationToken ct)
    {
        var url = $"/api/v1/track?albumId={albumId}";
        try
        {
            var tracks = await _http.GetFromJsonAsync<List<TrackResource>>(new Uri(Base, url), JsonOptions, ct);
            return tracks ?? new List<TrackResource>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch tracks for album {AlbumId}", albumId);
            throw;
        }
    }

    public async Task<IReadOnlyList<TrackResource>> GetReleaseTracksAsync(int albumReleaseId, CancellationToken ct)
    {
        var url = $"/api/v1/track?albumReleaseId={albumReleaseId}";
        try
        {
            var tracks = await _http.GetFromJsonAsync<List<TrackResource>>(new Uri(Base, url), JsonOptions, ct);
            return tracks ?? new List<TrackResource>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch tracks for album release {AlbumReleaseId}", albumReleaseId);
            throw;
        }
    }

    public async Task<AlbumResource?> GetAlbumAsync(int albumId, CancellationToken ct)
    {
        var url = $"/api/v1/album/{albumId}";
        try
        {
            return await _http.GetFromJsonAsync<AlbumResource>(new Uri(Base, url), JsonOptions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch album {AlbumId}", albumId);
            throw;
        }
    }

    public async Task<IReadOnlyList<ManualImportResource>> GetManualImportAsync(string downloadId, CancellationToken ct)
    {
        var url = $"/api/v1/manualimport?downloadId={Uri.EscapeDataString(downloadId)}";
        try
        {
            var rows = await _http.GetFromJsonAsync<List<ManualImportResource>>(new Uri(Base, url), JsonOptions, ct);
            return rows ?? new List<ManualImportResource>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch manual import rows for downloadId {DownloadId}", downloadId);
            throw;
        }
    }

    public async Task ImportAsync(IEnumerable<ManualImportFile> files, CancellationToken ct)
    {
        var fileList = files.ToArray();
        var command = new ManualImportCommand
        {
            Name = "ManualImport",
            Files = fileList.ToList(),
            ImportMode = "auto",
            ReplaceExistingFiles = false
        };

        var body = JsonSerializer.Serialize(command, WriteOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(new Uri(Base, "/api/v1/command"), content, ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Lidarr import command rejected ({Status}): {Body}", (int)response.StatusCode, text);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("Lidarr import command accepted {Status} for {Count} file(s)", (int)response.StatusCode,
            fileList.Length);
    }

    public async Task DeleteQueueItemAsync(int queueId, bool removeFromClient, bool blocklist, CancellationToken ct)
    {
        var url = $"/api/v1/queue/{queueId}?removeFromClient={Bool(removeFromClient)}&blocklist={Bool(blocklist)}";
        var response = await _http.DeleteAsync(new Uri(Base, url), ct);

        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            _logger.LogError("Lidarr queue delete failed ({Status}): {Body}", (int)response.StatusCode, text);
            response.EnsureSuccessStatusCode();
        }

        _logger.LogInformation("Lidarr queue item {QueueId} removed (and blocklisted)", queueId);
    }

    private static string Bool(bool value) => value ? "true" : "false";
}