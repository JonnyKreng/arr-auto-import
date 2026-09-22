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
}