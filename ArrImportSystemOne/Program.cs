using System.Text.Json;
using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Queue;
using ArrImportSystemOne.Solver;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ---- options ------------------------------------------------------------------------------
builder.Services.Configure<LidarrOptions>(builder.Configuration.GetSection(LidarrOptions.SectionName));

// ---- durable store + in-memory pipeline ---------------------------------------------------
builder.Services.AddSingleton<DecisionRepository>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<SystemOneAvailability>();
builder.Services.AddSingleton<WorkerProgress>();
builder.Services.AddSingleton<PollNotifier>();

// ---- solver -------------------------------------------------------------------------------
builder.Services.AddSingleton<IImportContextBuilder, ImportContextBuilder>();
builder.Services.AddSingleton<DecisionWriter>();
builder.Services.AddSingleton<IDecisionBroadcaster, DecisionBroadcaster>();

builder.Services.AddHttpClient<LidarrClient>((sp, http) =>
{
    var settings = sp.GetRequiredService<IOptionsMonitor<LidarrOptions>>().CurrentValue;
    http.BaseAddress = new Uri(settings.Url.TrimEnd('/') + "/");
    http.DefaultRequestHeaders.Add("X-Api-Key", settings.Key);
    http.Timeout = TimeSpan.FromSeconds(100);
});

// Swap this registration to change the decision model/provider. The worker only knows
// ISystemOneDecisionClient; Mica's /v1/systemone protocol is the default implementation.
builder.Services.AddHttpClient<ISystemOneDecisionClient, SystemOneDecisionClient>((sp, http) =>
{
    var settings = sp.GetRequiredService<IOptionsMonitor<LidarrOptions>>().CurrentValue;
    http.BaseAddress = new Uri(settings.SystemOne.Url.TrimEnd('/') + "/");
    // Per-request timeout comes from Lidarr:SystemOne:TimeoutSeconds (systemOne.md §8).
    http.Timeout = Timeout.InfiniteTimeSpan;
});

// ---- background services ------------------------------------------------------------------
builder.Services.AddHostedService<SystemOneHealthMonitor>();
builder.Services.AddHostedService<QueuePoller>();
builder.Services.AddHostedService<JobWorker>();

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

// ---- UI (build-free: only GET / + vendored assets) ----------------------------------------
var uiPath = Path.Combine(app.Environment.ContentRootPath, "Ui");
if (Directory.Exists(uiPath))
{
    var provider = new PhysicalFileProvider(uiPath);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
}

app.MapHub<DecisionHub>("/hub/decisions");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
