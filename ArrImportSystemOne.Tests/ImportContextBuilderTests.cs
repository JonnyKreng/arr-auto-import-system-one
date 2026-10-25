using System.Text.Json;
using ArrImportSystemOne.Lidarr;
using ArrImportSystemOne.Solver;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArrImportSystemOne.Tests;

public sealed class ImportContextBuilderTests
{
    private static readonly ImportContextBuilder Builder = new(NullLogger<ImportContextBuilder>.Instance);

    private static ManualImportFile Row(params LidarrTrack[] tracks) => new()
    {
        Id = 1,
        DownloadId = "dl1",
        Name = "01 - Song.flac",
        Path = "/music/Artist/Album/01 - Song.flac",
        AlbumReleaseId = 10,
        Artist = new LidarrArtist { Id = 5, ArtistName = "Artist" },
        Album = new LidarrAlbum { Id = 7, Title = "Album" },
        Quality = new QualityModel { Quality = new Quality { Id = 7, Name = "FLAC" } },
        Tracks = tracks.ToList()
    };

    [Fact]
    public void BuildCandidates_PreservesOrderAndDedupes()
    {
        var row = Row(
            new LidarrTrack { Id = 2, Title = "Second", TrackNumber = "2", Duration = 60_000 },
            new LidarrTrack { Id = 1, Title = "First", TrackNumber = "1", Duration = 120_000 },
            new LidarrTrack { Id = 2, Title = "Duplicate", TrackNumber = "2", Duration = 60_000 });

        var candidates = Builder.BuildCandidates(row);

        Assert.Equal(new[] { 2, 1 }, candidates.Select(c => c.TrackId));
        Assert.Equal("1:00", candidates[0].Duration);
        Assert.Equal("2:00", candidates[1].Duration);
        Assert.All(candidates, c => Assert.Equal(10, c.AlbumReleaseId));
    }

    [Fact]
    public void BuildCandidates_FromReleases_UsesTargetFirstAndDedupesEditions()
    {
        var row = Row(new LidarrTrack { Id = 101, Title = "Song", TrackNumber = "1", Duration = 60_000 });
        var releases = new[]
        {
            new ReleaseTracks(10,
            [
                new LidarrTrack { Id = 101, Title = "Song", TrackNumber = "1", Duration = 60_000 },
                new LidarrTrack { Id = 102, Title = "Second", TrackNumber = "2", Duration = 90_000 }
            ]),
            new ReleaseTracks(11,
            [
                // Same logical track on another edition: release-specific ID, number and a slightly
                // different duration. Title-only dedupe keeps it from becoming a second option.
                new LidarrTrack { Id = 201, Title = "Song", TrackNumber = "7", Duration = 61_000 },
                new LidarrTrack { Id = 202, Title = "Bonus", TrackNumber = "3", Duration = 120_000 }
            ])
        };

        var candidates = Builder.BuildCandidates(row, releases);

        Assert.Equal(new[] { 101, 102, 202 }, candidates.Select(c => c.TrackId));
        Assert.Equal(new[] { 10, 10, 11 }, candidates.Select(c => c.AlbumReleaseId));
        Assert.Equal("Bonus", candidates[2].Title);
    }

    [Fact]
    public void BuildQuestions_SingleCandidate_ProducesTwoOptions()
    {
        var candidates = Builder.BuildCandidates(Row(
            new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 60_000 }));

        var question = Builder.BuildQuestions(Row(), candidates, out var offered);

        Assert.Equal("choice", question.Type);
        Assert.NotNull(question.Criteria);
        Assert.Equal(2, question.Criteria!.Count);
        Assert.True(question.Criteria.ContainsKey("option_0"));
        Assert.True(question.Criteria.ContainsKey("no_match"));
        Assert.Single(offered);
    }

    [Fact]
    public void BuildQuestions_CapsCandidatesAt254_PlusNoMatch()
    {
        var candidates = Enumerable.Range(0, 300)
            .Select(i => new Candidate("", i + 1, 10, $"Track {i}", $"{i + 1}", "1:00", false))
            .ToList();

        var question = Builder.BuildQuestions(Row(), candidates, out var offered);

        Assert.Equal(ImportContextBuilder.MaxCandidates, offered.Count);
        Assert.Equal(255, question.Criteria!.Count);
        Assert.True(question.Criteria.ContainsKey("no_match"));
        Assert.Equal("option_0", offered[0].Key);
        Assert.Equal("option_253", offered[^1].Key);
    }

    [Fact]
    public void BuildModelState_ContainsDocumentedFields()
    {
        var row = Row(new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 200_000 });

        var state = Builder.BuildModelState(new LidarrQueueItem
        {
            Id = 3,
            Title = "Artist - Album",
            DownloadId = "dl1",
            Status = "completed"
        }, row);

        var json = JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("01 - Song.flac", json.GetProperty("file_name").GetString());
        Assert.Equal("Artist", json.GetProperty("artist").GetString());
        Assert.Equal(10, json.GetProperty("album_release_id").GetInt32());
        Assert.Equal("FLAC", json.GetProperty("quality").GetString());
        Assert.Equal("completed", json.GetProperty("queue_status").GetString());
        Assert.Equal(1, json.GetProperty("tracks").GetArrayLength());
        Assert.Equal("3:20", json.GetProperty("tracks")[0].GetProperty("duration").GetString());
    }

    [Fact]
    public void ToImportUpdate_MapsTrackReleaseAndQuality()
    {
        var row = Row(new LidarrTrack { Id = 101, Title = "Song", TrackNumber = "1", Duration = 200_000 });
        var candidate = Builder.BuildCandidates(row).Single();

        var update = Builder.ToImportUpdate(row, candidate);

        Assert.Equal("/music/Artist/Album/01 - Song.flac", update.Path);
        Assert.Equal(5, update.ArtistId);
        Assert.Equal(7, update.AlbumId);
        Assert.Equal(10, update.AlbumReleaseId);
        Assert.Equal(new[] { 101 }, update.TrackIds);
        Assert.Equal("FLAC", update.Quality!.Quality.Name);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(200_000, "3:20")]
    [InlineData(3_661_000, "1:01:01")]
    public void FormatDuration_FormatsAsExpected(int milliseconds, string expected) =>
        Assert.Equal(expected, ImportContextBuilder.FormatDuration(milliseconds));
}
