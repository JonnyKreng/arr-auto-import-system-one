using System.Net;
using ArrImportSystemOne.Options;
using ArrImportSystemOne.Solver;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrImportSystemOne.Tests;

public sealed class SystemOneDecisionClientTests
{
    private static SystemOneDecisionClient Create(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://mica:8010/") },
        new TestOptionsMonitor<LidarrOptions>(TestData.Options()),
        NullLogger<SystemOneDecisionClient>.Instance);

    private static SystemOneDecisionRequest Request() => new()
    {
        State = new { file_name = "01 - Song.flac", artist = "Artist" },
        Questions =
        {
            ["mapping"] = new SystemOneQuestion
            {
                Type = "choice",
                Instructions = "match it",
                Criteria = new Dictionary<string, string?>
                {
                    ["option_0"] = "title: 'Song' | length: 3:20 | track: 1",
                    ["no_match"] = "none"
                }
            }
        }
    };

    [Fact]
    public async Task DecideAsync_PostsSystemOneRequest_AndParsesAnswer()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(TestData.DecisionJson()));
        var client = Create(handler);

        var response = await client.DecideAsync(Request(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Null(response!.Error);
        Assert.Equal(SystemOneErrorKind.None, response.ErrorKind);
        Assert.Equal("option_0", response.Answers["mapping"].Choice);
        Assert.Equal(0.94, response.Answers["mapping"].Confidence, 3);
        Assert.Equal(41.7, response.LatencyMs, 3);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://mica:8010/v1/systemone", request.Uri);
        Assert.NotNull(request.Body);
        Assert.Contains("\"questions\"", request.Body);
        Assert.Contains("\"mapping\"", request.Body);
        Assert.Contains("no_match", request.Body);
        Assert.Contains("\"state\"", request.Body);

        // Mica reads Content-Length only; a chunked request is parsed as an empty body.
        Assert.NotNull(request.ContentLength);
        Assert.False(request.Chunked);

        // Optional question flags must be absent, not null: Mica rejects nulls on a Choice.
        Assert.DoesNotContain("\"multi\"", request.Body);
        Assert.DoesNotContain("allow_cannot_grade", request.Body);
        Assert.DoesNotContain("abstain_threshold", request.Body);
    }

    [Fact]
    public async Task DecideAsync_BadRequest_IsInvalidRequest()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"detail":"input exceeds 32768 tokens"}""", HttpStatusCode.BadRequest));
        var client = Create(handler);

        var response = await client.DecideAsync(Request(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(SystemOneErrorKind.InvalidRequest, response!.ErrorKind);
        Assert.Contains("400", response.Error);
        Assert.Contains("32768", response.Error);
    }

    [Fact]
    public async Task DecideAsync_ServerError_IsUnavailable()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"detail":"gpu exploded"}""", HttpStatusCode.InternalServerError));
        var client = Create(handler);

        var response = await client.DecideAsync(Request(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(SystemOneErrorKind.Unavailable, response!.ErrorKind);
        Assert.Contains("500", response.Error);
        Assert.Contains("gpu exploded", response.Error);
    }

    [Fact]
    public async Task DecideAsync_UnknownChoice_IsInvalidResponse()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(TestData.DecisionJson("option_9")));
        var client = Create(handler);

        var response = await client.DecideAsync(Request(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(SystemOneErrorKind.InvalidResponse, response!.ErrorKind);
        Assert.Contains("option_9", response.Error);
    }

    [Fact]
    public async Task DecideAsync_TransportFailure_IsUnavailable()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));
        var client = Create(handler);

        var response = await client.DecideAsync(Request(), CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(SystemOneErrorKind.Unavailable, response!.ErrorKind);
        Assert.Contains("connection refused", response.Error);
    }

    [Fact]
    public async Task CheckHealthAsync_Ok_IsHealthy()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.EndsWith("/health", request.RequestUri!.AbsolutePath);
            return StubHandler.Json("""{"status":"ok","model":"mica-v0.5-4b","warming":false}""");
        });
        var client = Create(handler);

        var health = await client.CheckHealthAsync(CancellationToken.None);

        Assert.True(health.Healthy);
        Assert.Null(health.Detail);
    }

    [Fact]
    public async Task CheckHealthAsync_Warming_IsUnhealthy()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"status":"ok","model":"mica-v0.5-4b","warming":true}"""));
        var client = Create(handler);

        var health = await client.CheckHealthAsync(CancellationToken.None);

        Assert.False(health.Healthy);
        Assert.Contains("warming", health.Detail);
    }

    [Fact]
    public async Task CheckHealthAsync_ServerError_IsUnhealthy()
    {
        var handler = new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = Create(handler);

        var health = await client.CheckHealthAsync(CancellationToken.None);

        Assert.False(health.Healthy);
        Assert.Contains("503", health.Detail);
    }
}
