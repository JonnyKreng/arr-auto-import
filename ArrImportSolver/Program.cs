using ArrImportSolver;
using ArrImportSolver.Lidarr;
using ArrImportSolver.Options;
using ArrImportSolver.Solver;
using ArrImportSolver.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

var lidarrSection = builder.Configuration.GetSection(LidarrOptions.SectionName);
builder.Services.Configure<LidarrOptions>(lidarrSection);

var uiSection = builder.Configuration.GetSection(UiOptions.SectionName);
builder.Services.Configure<UiOptions>(uiSection);

var ui = new UiOptions();
uiSection.Bind(ui);
builder.WebHost.UseUrls($"http://0.0.0.0:{ui.Port}");

builder.Services.AddHttpClient<LidarrClient>();
builder.Services.AddHttpClient<LayaDecisionClient>()
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<DecisionRepository>();
builder.Services.AddSingleton<PollNotifier>();
builder.Services.AddSingleton<IImportDecisionEngine, ImportDecisionEngine>();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

app.MapGet("/", async context =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Ui", "index.html");
    if (!File.Exists(path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("ui/index.html not found");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(path);
});

app.MapGet("/api/decisions", (DecisionRepository store, int limit = 200) =>
    Results.Ok(store.GetRecent(limit)));

app.MapPost("/api/restart", (DecisionRepository store, PollNotifier notifier, string downloadId) =>
{
    if (!store.RestartAlbum(downloadId))
    {
        return Results.NotFound();
    }

    notifier.Signal();
    return Results.NoContent();
});

app.Run();