using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArrImportSolver.Options;
using Microsoft.Extensions.Options;

namespace ArrImportSolver.Solver;

public class LayaDecisionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<LidarrOptions> _options;
    private readonly ILogger<LayaDecisionClient> _logger;

    public LayaDecisionClient(HttpClient http, IOptionsMonitor<LidarrOptions> options,
        ILogger<LayaDecisionClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<LayaDecideResponse?> DecideAsync(LayaDecideRequest request, CancellationToken ct)
    {
        var url = $"{_options.CurrentValue.SidecarUrl.TrimEnd('/')}/decide";
        var body = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            using var response = await _http.PostAsync(url, content, timeout.Token);
            return await response.Content.ReadFromJsonAsync<LayaDecideResponse>(JsonOptions, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Laya sidecar call failed at {Url}", url);
            return new LayaDecideResponse { Error = ex.Message };
        }
    }

    /// <summary>
    /// Waits for the sidecar's /ready endpoint to report ready before any decision is requested.
    /// The sidecar only starts serving once its model has loaded, so early attempts may be
    /// refused or time out; this polls until it answers <c>{"ready": true}</c>.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(CancellationToken ct)
    {
        var url = $"{_options.CurrentValue.SidecarUrl.TrimEnd('/')}/ready";
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.SidecarReadyTimeoutSeconds));
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var requestCt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestCt.CancelAfter(TimeSpan.FromSeconds(15));

                using var response = await _http.GetAsync(url, requestCt.Token);
                if (response.IsSuccessStatusCode)
                {
                    var ready = await response.Content.ReadFromJsonAsync<ReadyResponse>(JsonOptions, requestCt.Token);
                    if (ready is { Ready: true })
                    {
                        _logger.LogInformation("Decision sidecar is ready at {Url} (attempt {Attempt})", url, attempt);
                        return true;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Decision sidecar not ready yet at {Url}", url);
            }

            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogError(
                    "Decision sidecar not ready after {TimeoutSeconds}s at {Url}; decisions will be left to a human",
                    timeout.TotalSeconds, url);
                return false;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        return false;
    }

    private sealed record ReadyResponse(bool Ready);
}