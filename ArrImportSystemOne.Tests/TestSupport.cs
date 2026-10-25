using System.Net;
using System.Text;
using ArrImportSystemOne.Hub;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Store;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ArrImportSystemOne.Tests;

internal sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "ArrImportSystemOne.Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class RecordingBroadcaster : IDecisionBroadcaster
{
    public List<DecisionView> Decisions { get; } = new();
    public List<JobRecord> Jobs { get; } = new();
    public List<Queue.QueueUpdated> QueueUpdates { get; } = new();

    public Task QueueUpdatedAsync(Queue.QueueUpdated payload, CancellationToken ct = default)
    {
        QueueUpdates.Add(payload);
        return Task.CompletedTask;
    }

    public Task JobFinishedAsync(JobRecord job, CancellationToken ct = default)
    {
        Jobs.Add(job);
        return Task.CompletedTask;
    }

    public Task DecisionResolvedAsync(DecisionView decision, CancellationToken ct = default)
    {
        Decisions.Add(decision);
        return Task.CompletedTask;
    }
}

internal sealed class TempRepository : IDisposable
{
    private readonly string _dir;

    public TempRepository()
    {
        _dir = Path.Combine(Path.GetTempPath(), "arrimport-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Repository = new DecisionRepository(
            new TestHostEnvironment { ContentRootPath = _dir },
            NullLogger<DecisionRepository>.Instance);
    }

    public DecisionRepository Repository { get; }

    public void Dispose()
    {
        Repository.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best effort temp cleanup.
        }
    }
}

/// <summary>HTTP stub that records requests and lets each test decide the response per path.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? "",
            body,
            request.Content?.Headers.ContentLength,
            request.Headers.TransferEncodingChunked == true));
        return responder(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}

internal sealed record RecordedRequest(
    string Method,
    string Uri,
    string? Body,
    long? ContentLength,
    bool Chunked);

internal static class TestData
{
    public static LidarrOptions Options(bool dryRun = false, double minConfidence = 0.75, int pollSeconds = 60) => new()
    {
        Url = "http://lidarr",
        DryRun = dryRun,
        PollIntervalSeconds = pollSeconds,
        SystemOne = new SystemOneOptions { Url = "http://mica", TimeoutSeconds = 5, HealthCheckSeconds = 2 },
        Model = new ModelOptions { MinConfidence = minConfidence }
    };

    public const string ManualImportJson = """
        [
          {
            "id": 1,
            "path": "/music/Artist/Album/01 - Song.flac",
            "name": "01 - Song.flac",
            "downloadId": "dl1",
            "additionalFile": false,
            "artist": { "id": 5, "artistName": "Artist" },
            "album": { "id": 7, "title": "Album" },
            "albumReleaseId": 10,
            "quality": {
              "quality": { "id": 7, "name": "FLAC", "source": "Lossless", "modifier": "None" },
              "revision": { "version": 1, "real": 0, "isRepack": false }
            },
            "rejections": [],
            "tracks": [
              { "id": 101, "title": "Song", "trackNumber": "1", "duration": 200000, "hasFile": false },
              { "id": 102, "title": "Other", "trackNumber": "2", "duration": 241000, "hasFile": false }
            ]
          }
        ]
        """;

    public static string DecisionJson(string choice = "option_0", double confidence = 0.94)
    {
        var c = confidence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rest = (1 - confidence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            {
              "model": "mica-v0.5-4b",
              "answers": {
                "mapping": {
                  "type": "choice",
                  "choice": "{{choice}}",
                  "probabilities": { "option_0": {{c}}, "no_match": {{rest}} },
                  "confidence": {{c}}
                }
              },
              "usage": { "input_tokens": 168, "output_tokens": 0 },
              "latency_ms": 41.7
            }
            """;
    }
}
