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
        Album = new LidarrAlbum
        {
            Id = 7,
            Title = "Album",
            Releases = [new AlbumRelease { Id = 10, Title = "Album" }]
        },
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
            new ReleaseTracks(10, "Album",
            [
                new LidarrTrack { Id = 101, Title = "Song", TrackNumber = "1", Duration = 60_000 },
                new LidarrTrack { Id = 102, Title = "Second", TrackNumber = "2", Duration = 90_000 }
            ]),
            new ReleaseTracks(11, "Album (Deluxe)",
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
        Assert.Equal("Album (Deluxe)", candidates[2].ReleaseTitle);
        Assert.Equal("Bonus", candidates[2].Title);
    }

    [Fact]
    public void BuildCandidates_FromReleases_SkipsOtherAlbums()
    {
        var row = Row(new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 60_000 });
        row.Album = new LidarrAlbum { Id = 7, Title = "Speak Now" };

        var releases = new[]
        {
            new ReleaseTracks(10, "Speak Now",
                [new LidarrTrack { Id = 1, Title = "Mine", TrackNumber = "1", Duration = 230_000 }]),
            new ReleaseTracks(11, "Fearless",
                [new LidarrTrack { Id = 2, Title = "Fifteen", TrackNumber = "2", Duration = 295_000 }]),
            new ReleaseTracks(12, "Speak Now (Taylor's Version)",
                [new LidarrTrack { Id = 3, Title = "Timeless (Taylor's Version) (From The Vault)", TrackNumber = "22", Duration = 375_000 }])
        };

        var candidates = Builder.BuildCandidates(row, releases);

        Assert.Equal(new[] { 1, 3 }, candidates.Select(c => c.TrackId));
        Assert.DoesNotContain(candidates, c => c.Title == "Fifteen");
        Assert.Equal("Speak Now (Taylor's Version)", candidates[1].ReleaseTitle);
    }

    [Fact]
    public void BuildQuestions_SingleCandidate_ProducesThreeOptions()
    {
        var candidates = Builder.BuildCandidates(Row(
            new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 60_000 }));

        var question = Builder.BuildQuestions(Row(), candidates, out var offered);

        Assert.Equal("choice", question.Type);
        Assert.NotNull(question.Criteria);
        Assert.Equal(3, question.Criteria!.Count);
        Assert.True(question.Criteria.ContainsKey("option_0"));
        Assert.True(question.Criteria.ContainsKey("no_match"));
        Assert.True(question.Criteria.ContainsKey("album_mismatch"));
        Assert.Single(offered);
    }

    [Fact]
    public void BuildQuestions_CapsCandidatesAt253_PlusTwoReservedOutcomes()
    {
        var candidates = Enumerable.Range(0, 300)
            .Select(i => new Candidate("", i + 1, 10, "Album", $"Track {i}", $"{i + 1}", "1:00", false))
            .ToList();

        var question = Builder.BuildQuestions(Row(), candidates, out var offered);

        Assert.Equal(ImportContextBuilder.MaxCandidates, offered.Count);
        Assert.Equal(255, question.Criteria!.Count);
        Assert.True(question.Criteria.ContainsKey("no_match"));
        Assert.True(question.Criteria.ContainsKey("album_mismatch"));
        Assert.Equal("option_0", offered[0].Key);
        Assert.Equal("option_252", offered[^1].Key);
    }

    [Fact]
    public void BuildModelState_ContainsDocumentedFields()
    {
        var row = Row(new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 200_000 });
        row.AudioTags = new ParsedTrackInfo
        {
            Title = "Song (Taylor's Version)",
            ArtistTitle = "Artist",
            AlbumTitle = "Album (Taylor's Version)",
            ReleaseGroup = "Album",
            TrackNumbers = [1],
            DiscNumber = 1,
            Duration = TimeSpan.FromSeconds(201),
            Year = 2024
        };

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
        Assert.Equal("Album", json.GetProperty("album").GetString());
        Assert.Equal("Album", json.GetProperty("target_release").GetString());
        Assert.Equal(10, json.GetProperty("album_release_id").GetInt32());
        Assert.Equal("FLAC", json.GetProperty("quality").GetString());
        Assert.Equal("completed", json.GetProperty("queue_status").GetString());
        Assert.Equal(1, json.GetProperty("tracks").GetArrayLength());
        Assert.Equal("3:20", json.GetProperty("tracks")[0].GetProperty("duration").GetString());

        var tags = json.GetProperty("file_tags");
        Assert.Equal("Song (Taylor's Version)", tags.GetProperty("title").GetString());
        Assert.Equal("Album (Taylor's Version)", tags.GetProperty("album").GetString());
        Assert.Equal(1, tags.GetProperty("track_numbers")[0].GetInt32());
        Assert.Equal("3:21", tags.GetProperty("duration").GetString());
    }

    [Fact]
    public void BuildQuestions_InstructionsCoverEditionMismatch()
    {
        var candidates = Builder.BuildCandidates(Row(
            new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 60_000 }));

        var question = Builder.BuildQuestions(Row(), candidates, out _);

        Assert.Contains("file_tags", question.Instructions);
        Assert.Contains("album_mismatch", question.Instructions);
        Assert.Contains("Taylor's Version", question.Instructions);
        Assert.Contains("no_match", question.Instructions);
    }

    [Fact]
    public void BuildQuestions_CriteriaIncludeReleaseTitle()
    {
        var candidates = Builder.BuildCandidates(Row(
            new LidarrTrack { Id = 1, Title = "Song", TrackNumber = "1", Duration = 60_000 }));

        var question = Builder.BuildQuestions(Row(), candidates, out _);

        Assert.Contains("release: \"Album\"", question.Criteria!["option_0"]);
        Assert.Contains("title: \"Song\"", question.Criteria["option_0"]);
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
