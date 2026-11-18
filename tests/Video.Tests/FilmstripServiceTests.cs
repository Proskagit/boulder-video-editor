using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Thumbnails;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>D068 item 10: <see cref="FilmstripService"/> over the fake decoder (each frame carries its number in its pixel) —
/// the pictures' count and source times, the cache (one file, a hit without decoding, offline media), and the clip's mapping
/// of a place to a source time.</summary>
public sealed class FilmstripServiceTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-filmstrips", Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly FakeVideoDecoder _decoder = new();

    public FilmstripServiceTests()
    {
        _cache = Path.Combine(_dir, "cache", "thumbnails");
        Directory.CreateDirectory(Path.Combine(_dir, "media"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private FilmstripService Service() => new(_decoder, NullLogger<FilmstripService>.Instance);

    private MediaAsset Video(double seconds)
    {
        var path = Path.Combine(_dir, "media", $"{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "source");
        var frames = (int)(seconds * 25);
        _decoder.Add(path, new FakeSource(Rate, frames));
        return new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(seconds), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36,
                StartTime = MediaTime.Zero
            }
        };
    }

    private static int Number(Thumbnail picture) => BitConverter.ToInt32(picture.Pixels.Span);

    [Theory]
    [InlineData(1, 2)]        // short: at least two pictures
    [InlineData(10, 5)]       // one per two seconds
    [InlineData(600, 16)]     // ten minutes: at most sixteen
    public void A_video_gets_one_picture_per_two_seconds_between_two_and_sixteen(double seconds, int count) =>
        Assert.Equal(count, FilmstripService.FrameCount(MediaTime.FromSeconds(seconds)));

    [Fact]
    public async Task The_pictures_are_the_middles_of_equal_parts_and_are_cached_in_one_file_read_back_without_decoding()
    {
        var asset = Video(10);

        var made = await Service().GetOrCreateAsync(asset, _cache);

        Assert.NotNull(made);
        Assert.Equal(new[] { 1.0, 3.0, 5.0, 7.0, 9.0 }, made!.Frames.Select(f => f.SourceTime.TotalSeconds));
        Assert.Equal(new[] { 25, 75, 125, 175, 225 }, made.Frames.Select(f => Number(f.Picture)));   // the frame at each time
        Assert.All(_decoder.Requests, r => Assert.Equal((96, 54, HardwareDecoding.Disabled), (r.MaxWidth, r.MaxHeight, r.Hardware)));
        var file = Assert.Single(Directory.GetFiles(_cache));
        Assert.EndsWith(".strip", file);

        var decoded = _decoder.Requests.Count;
        var again = await Service().GetOrCreateAsync(asset, _cache);
        Assert.Equal(decoded, _decoder.Requests.Count);                  // a hit: nothing decoded
        Assert.Equal(made.Frames.Select(f => Number(f.Picture)), again!.Frames.Select(f => Number(f.Picture)));
        Assert.NotNull(Service().TryGetCached(asset, _cache));
    }

    [Fact]
    public async Task Offline_media_is_never_decoded_and_audio_has_no_filmstrip()
    {
        var asset = Video(10);
        await Service().GetOrCreateAsync(asset, _cache);
        File.Delete(asset.FilePath);
        var decoded = _decoder.Requests.Count;

        Assert.NotNull(await Service().GetOrCreateAsync(asset, _cache));    // the last cached one
        Assert.Equal(decoded, _decoder.Requests.Count);

        var sound = Video(10);
        sound.Kind = MediaKind.Audio;
        Assert.Null(await Service().GetOrCreateAsync(sound, _cache));
    }

    [Fact]
    public void A_damaged_file_is_a_miss()
    {
        Directory.CreateDirectory(_cache);
        var path = Path.Combine(_cache, "x.strip");
        File.WriteAllBytes(path, "BVSF"u8.ToArray().Concat(new byte[20]).ToArray());
        Assert.Null(FilmstripService.TryRead(path));
    }

    [Fact]
    public void A_clip_shows_at_each_place_the_picture_nearest_to_its_source_time()
    {
        var picture = new Thumbnail(1, 1, new byte[4]);
        var strip = new Filmstrip(new[]
        {
            new FilmstripFrame(MediaTime.FromSeconds(1), picture), new FilmstripFrame(MediaTime.FromSeconds(3), picture),
            new FilmstripFrame(MediaTime.FromSeconds(5), picture)
        });
        // The clip starts 2 s into the video, plays at 2×, 100 px per second on the timeline.
        Assert.True(ClipSpeed.TryFromRatio(2, 1, out var speed));
        var clip = new ClipFilmstrip(strip, MediaTime.FromSeconds(2), speed, 100);

        Assert.Equal(MediaTime.FromSeconds(2), clip.SourceTimeAt(0));
        Assert.Equal(MediaTime.FromSeconds(4), clip.SourceTimeAt(100));      // 1 s of timeline = 2 s of source
        Assert.Equal(MediaTime.FromSeconds(3), strip.Nearest(clip.SourceTimeAt(40)).SourceTime);   // 2.8 s → the 3 s picture
        Assert.Equal(MediaTime.FromSeconds(5), strip.Nearest(MediaTime.FromSeconds(60)).SourceTime);
    }
}
