using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Thumbnails;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.4a: <see cref="ThumbnailService"/> over the fake decoder, whose frames carry their number in their
/// one pixel — so the thumbnail says which source frame was chosen. The cache (hit, miss, invalidation, damaged
/// files, offline media) and the frame rule (D009 at <c>T = min(⌊Duration / 10⌋, 5 s)</c>).
/// </summary>
public sealed class ThumbnailServiceTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-thumbnails", Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly FakeVideoDecoder _decoder = new();

    public ThumbnailServiceTests()
    {
        _cache = Path.Combine(_dir, "cache", "thumbnails");
        Directory.CreateDirectory(Path.Combine(_dir, "media"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ThumbnailService Service(int ruleVersion = ThumbnailService.CurrentRuleVersion) =>
        new(_decoder, NullLogger<ThumbnailService>.Instance) { RuleVersion = ruleVersion };

    /// <summary>An analysed video: a real file (for its size and time) decoded by the fake as <paramref name="frames"/>
    /// frames at 25 fps, the first one at <paramref name="startPts"/>.</summary>
    private MediaAsset Video(long durationTicks, int frames = 250, long startPts = 0, MediaKind kind = MediaKind.Video)
    {
        var path = Path.Combine(_dir, "media", $"{Guid.NewGuid():N}.mp4");
        File.WriteAllText(path, "source");
        _decoder.Add(path, new FakeSource(Rate, frames, startPts));
        return new MediaAsset
        {
            FilePath = path, Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = new MediaTime(durationTicks), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36,
                StartTime = MediaTime.FromFrame(startPts, Rate)
            }
        };
    }

    private static int Number(Thumbnail thumbnail) => BitConverter.ToInt32(thumbnail.Pixels.Span);

    private string[] CacheFiles() => Directory.Exists(_cache) ? Directory.GetFiles(_cache) : Array.Empty<string>();

    // --- hit, miss, invalidation -----------------------------------------------------------------------------

    [Fact]
    public async Task A_miss_decodes_the_frame_and_caches_it_then_every_later_request_is_a_hit_without_decoding()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);

        var made = await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(25, Number(made!));                                 // T = 1 s → frame 25
        var request = Assert.Single(_decoder.Requests);
        Assert.Equal((asset.FilePath, new SourceSamplePoint(10_000_000, 1)), (request.FilePath, request.FirstSamplePoint));
        Assert.Equal((160, 90, HardwareDecoding.Disabled, true), (request.MaxWidth, request.MaxHeight, request.Hardware, request.StrictEnd));
        var file = Assert.Single(CacheFiles());
        Assert.StartsWith($"{asset.Id:N}-", Path.GetFileName(file));
        Assert.EndsWith("-v1.thumb", file);

        var fresh = Service();                                            // e.g. the project reopened
        Assert.Equal(25, Number((await fresh.GetOrCreateAsync(asset, _cache))!));
        Assert.Equal(25, Number(fresh.TryGetCached(asset, _cache)!));
        Assert.Single(_decoder.Requests);                                // hits never decode
        Assert.Equal(0, _decoder.LiveStreams);
    }

    [Fact]
    public async Task A_changed_file_size_is_a_miss_and_replaces_the_old_thumbnail()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service().GetOrCreateAsync(asset, _cache);
        var time = File.GetLastWriteTimeUtc(asset.FilePath);

        File.WriteAllText(asset.FilePath, "source, edited");
        File.SetLastWriteTimeUtc(asset.FilePath, time);                  // same time, other size
        Assert.Null(Service().TryGetCached(asset, _cache));
        await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(2, _decoder.Requests.Count);
        Assert.Single(CacheFiles());
    }

    [Fact]
    public async Task A_changed_last_write_time_is_a_miss()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service().GetOrCreateAsync(asset, _cache);

        File.SetLastWriteTimeUtc(asset.FilePath, File.GetLastWriteTimeUtc(asset.FilePath).AddSeconds(-30)); // same size
        Assert.Null(Service().TryGetCached(asset, _cache));
        await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(2, _decoder.Requests.Count);
        Assert.Single(CacheFiles());
    }

    [Fact]
    public async Task Another_rule_version_is_a_miss_and_replaces_the_old_thumbnail()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service(ruleVersion: 1).GetOrCreateAsync(asset, _cache);

        var next = Service(ruleVersion: 2);
        Assert.Null(next.TryGetCached(asset, _cache));
        await next.GetOrCreateAsync(asset, _cache);

        Assert.Equal(2, _decoder.Requests.Count);
        Assert.EndsWith("-v2.thumb", Assert.Single(CacheFiles()));
    }

    public static TheoryData<string, Func<byte[], byte[]>> Damage => new()
    {
        { "empty", _ => Array.Empty<byte>() },
        { "cut short", b => b[..^1] },
        { "header only", b => b[..16] },
        { "one byte too many", b => [.. b, 0] },
        { "wrong magic", b => { var c = (byte[])b.Clone(); c[0] = (byte)'X'; return c; } },
        { "other format version", b => { var c = (byte[])b.Clone(); c[4] = 9; return c; } },
        { "zero width", b => { var c = (byte[])b.Clone(); c[8] = c[9] = c[10] = c[11] = 0; return c; } },
        { "absurd height", b => { var c = (byte[])b.Clone(); c[12] = c[13] = c[14] = 0x7F; c[15] = 0x7F; return c; } },
        { "text", _ => "not a thumbnail at all"u8.ToArray() }
    };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task A_damaged_cache_file_is_a_miss_and_is_made_again(string what, Func<byte[], byte[]> damage)
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service().GetOrCreateAsync(asset, _cache);
        var file = Assert.Single(CacheFiles());
        File.WriteAllBytes(file, damage(File.ReadAllBytes(file)));

        Assert.Null(Service().TryGetCached(asset, _cache));
        var made = await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(25, Number(made!));
        Assert.Equal(2, _decoder.Requests.Count);
        Assert.True(Number(Service().TryGetCached(asset, _cache)!) == 25, $"cache not repaired after: {what}");
    }

    [Fact]
    public async Task Writes_are_atomic_and_an_unwritable_cache_only_costs_the_cache()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service().GetOrCreateAsync(asset, _cache);
        Assert.DoesNotContain(CacheFiles(), f => f.EndsWith(".tmp"));

        var blocked = Path.Combine(_dir, "a-file-not-a-folder");
        File.WriteAllText(blocked, "x");
        var made = await Service().GetOrCreateAsync(asset, blocked);
        Assert.Equal(25, Number(made!));                                 // still returned
        Assert.Null(Service().TryGetCached(asset, blocked));
    }

    // --- offline media and assets without a picture ---------------------------------------------------------------

    [Fact]
    public async Task Offline_media_shows_its_last_cached_thumbnail_and_is_never_decoded()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        await Service().GetOrCreateAsync(asset, _cache);

        asset.IsMissing = true;                                          // marked missing when the project was opened
        Assert.Equal(25, Number((await Service().GetOrCreateAsync(asset, _cache))!));
        Assert.Equal(25, Number(Service().TryGetCached(asset, _cache)!));

        asset.IsMissing = false;
        File.Delete(asset.FilePath);                                     // gone during the session
        Assert.Equal(25, Number((await Service().GetOrCreateAsync(asset, _cache))!));

        Assert.Single(_decoder.Requests);
    }

    [Fact]
    public async Task Offline_media_without_a_cached_thumbnail_has_none_and_is_not_decoded()
    {
        var missing = Video(10 * TimeSpan.TicksPerSecond);
        missing.IsMissing = true;
        var gone = Video(10 * TimeSpan.TicksPerSecond);
        File.Delete(gone.FilePath);

        Assert.Null(await Service().GetOrCreateAsync(missing, _cache));
        Assert.Null(await Service().GetOrCreateAsync(gone, _cache));
        Assert.Null(Service().TryGetCached(gone, _cache));
        Assert.Empty(_decoder.Requests);
    }

    [Fact]
    public async Task Audio_and_unanalysed_media_have_no_thumbnail_and_are_not_decoded()
    {
        var audio = Video(10 * TimeSpan.TicksPerSecond, kind: MediaKind.Audio);
        var pending = Video(10 * TimeSpan.TicksPerSecond);
        pending.AnalysisStatus = MediaAnalysisStatus.Pending;
        pending.Metadata = null;
        var failed = Video(10 * TimeSpan.TicksPerSecond);
        failed.AnalysisStatus = MediaAnalysisStatus.Failed;

        foreach (var asset in new[] { audio, pending, failed })
            Assert.Null(await Service().GetOrCreateAsync(asset, _cache));
        Assert.Empty(_decoder.Requests);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task A_source_that_cannot_be_decoded_has_no_thumbnail_and_nothing_is_cached()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        _decoder.FailOpen(asset.FilePath, VideoDecodeError.DecoderFailed);

        Assert.Null(await Service().GetOrCreateAsync(asset, _cache));
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_nothing_behind()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond);
        _decoder.Gate(asset.FilePath);
        using var cts = new CancellationTokenSource();

        var making = Service().GetOrCreateAsync(asset, _cache, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => making);
        Assert.Empty(CacheFiles());
        Assert.Equal(0, _decoder.LiveStreams);
    }

    // --- the frame: D009 at T = min(⌊Duration / 10⌋, 5 s) -------------------------------------------------------------

    [Theory]
    [InlineData(10 * TimeSpan.TicksPerSecond, 10_000_000)]            // 1 s
    [InlineData(100 * TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond)] // capped at 5 s
    [InlineData(50 * TimeSpan.TicksPerSecond, 5 * TimeSpan.TicksPerSecond)]  // exactly the cap
    [InlineData(12_000_009, 1_200_000)]                                // floor
    [InlineData(19, 1)]
    [InlineData(9, 0)]
    [InlineData(0, 0)]
    public void The_source_time_is_a_tenth_of_the_duration_rounded_down_at_most_five_seconds(long durationTicks, long expected)
    {
        Assert.Equal(expected, ThumbnailService.SourceTime(new MediaMetadata { Duration = new MediaTime(durationTicks) }).Ticks);
        Assert.Equal(0, ThumbnailService.SourceTime(null).Ticks);
    }

    [Theory]
    [InlineData(10 * TimeSpan.TicksPerSecond, 250, 25)]     // T = 1 s: frame 25 starts exactly at T and is taken
    [InlineData(100 * TimeSpan.TicksPerSecond, 2500, 125)]  // T = 5 s (cap)
    [InlineData(3 * TimeSpan.TicksPerSecond, 75, 7)]        // T = 0.3 s: 0.28 ≤ T < 0.32
    [InlineData(12_000_000, 30, 3)]                          // T = 0.12 s = frame 3's start exactly
    [InlineData(11_999_999, 30, 2)]                          // T one tick earlier: frame 2
    [InlineData(1_200_000, 3, 0)]                            // a 3-frame clip: T = 12 ms → the first frame
    [InlineData(20 * TimeSpan.TicksPerSecond, 25, 24)]      // T = 2 s after the last of 25 frames: the last one (hold-last)
    public async Task The_frame_is_the_last_one_at_or_before_T(long durationTicks, int frames, int expected)
    {
        var asset = Video(durationTicks, frames);

        var made = await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(expected, Number(made!));
    }

    [Fact]
    public async Task Source_time_counts_from_the_file_start_time()
    {
        var asset = Video(10 * TimeSpan.TicksPerSecond, startPts: 35); // the stream starts at 1.4 s

        var made = await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(25, Number(made!));                               // 1 s after the start, not at 1 s absolute
        Assert.Equal(MediaTime.FromFrame(35, Rate), Assert.Single(_decoder.Requests).StartTime);
    }

    [Fact]
    public async Task An_image_is_its_one_frame()
    {
        var image = Video(0, frames: 1, kind: MediaKind.Image);

        var made = await Service().GetOrCreateAsync(image, _cache);

        Assert.Equal(0, Number(made!));
        Assert.Single(CacheFiles());
    }
}
