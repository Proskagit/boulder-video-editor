using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Waveforms;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D024 Step 9.5a: <see cref="WaveformService"/> over the fake audio decoder — the peaks (<c>max(|L|, |R|)</c> per 256
/// source samples, placed by the stream's first sample), which media get one, and the cache (hit, miss, invalidation,
/// damaged files, offline media, failures, cancellation) — the thumbnail cache's rules.
/// </summary>
public sealed class WaveformServiceTests : IDisposable
{
    private const int PerPeak = WaveformService.DefaultSamplesPerPeak;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-waveforms", Guid.NewGuid().ToString("N"));
    private readonly string _cache;
    private readonly FakeAudioDecoder _decoder = new();

    public WaveformServiceTests()
    {
        _cache = Path.Combine(_dir, "cache", "waveforms");
        Directory.CreateDirectory(Path.Combine(_dir, "media"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WaveformService Service(int ruleVersion = WaveformService.CurrentRuleVersion) =>
        new(_decoder, NullLogger<WaveformService>.Instance) { RuleVersion = ruleVersion };

    /// <summary>An analysed media file with sound: a real file (for its size and time) decoded by the fake as
    /// <paramref name="source"/> (by default a constant 0.25 for 10 000 samples).</summary>
    private MediaAsset Media(FakeAudioSource? source = null, MediaKind kind = MediaKind.Audio, string? audioCodec = "aac",
        MediaTime? startTime = null)
    {
        var path = Path.Combine(_dir, "media", $"{Guid.NewGuid():N}.{(kind == MediaKind.Audio ? "wav" : "mp4")}");
        File.WriteAllText(path, "source");
        _decoder.Add(path, source ?? new FakeAudioSource(10_000, Constant: 0.25f));
        return new MediaAsset
        {
            FilePath = path, Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(1), AudioCodec = audioCodec, StartTime = startTime }
        };
    }

    private string[] CacheFiles() => Directory.Exists(_cache) ? Directory.GetFiles(_cache) : Array.Empty<string>();

    // --- peaks ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_peak_is_the_largest_left_or_right_magnitude_of_its_256_source_samples()
    {
        // Group 0: quiet (0.1), one loud left sample (0.5). Group 1: loud negative right sample (−0.9).
        // Group 2: silence. Group 3 (partial, 100 samples): 0.3 on the right only.
        var asset = Media(new FakeAudioSource(3 * PerPeak + 100, Shape: i => i switch
        {
            10 => (0.5f, 0.1f),
            < PerPeak => (0.1f, -0.1f),
            PerPeak + 7 => (0.2f, -0.9f),
            < 2 * PerPeak => (0.05f, 0.05f),
            < 3 * PerPeak => (0f, 0f),
            _ => (0f, 0.3f)
        }));

        var waveform = (await Service().GetOrCreateAsync(asset, _cache))!;

        Assert.Equal((PerPeak, 3L * PerPeak + 100), (waveform.SamplesPerPeak, waveform.SampleCount));
        Assert.Equal(new byte[] { 128, 230, 0, 77 }, waveform.Peaks.ToArray()); // ⌈0.5·255⌉, ⌈0.9·255⌉, 0, ⌈0.3·255⌉
    }

    [Fact]
    public async Task The_whole_file_is_decoded_once_from_its_start_at_1x_strictly()
    {
        var start = MediaTime.FromSeconds(1.4);
        var asset = Media(startTime: start);

        await Service().GetOrCreateAsync(asset, _cache);

        var request = Assert.Single(_decoder.Requests);
        Assert.Equal((asset.FilePath, start, MediaTime.Zero, ClipSpeed.Normal, true),
            (request.FilePath, request.StartTime, request.SourcePosition, request.Speed, request.StrictEnd));
        Assert.Equal(0, _decoder.LiveStreams);
    }

    [Fact]
    public async Task Samples_before_the_file_start_are_dropped()
    {
        // The stream begins 100 samples before the start (preroll); those samples are loud.
        var asset = Media(new FakeAudioSource(1000, StreamStartSample: -300, Shape: i => i < 0 ? (1f, 1f) : (0.1f, 0.1f)));

        var waveform = (await Service().GetOrCreateAsync(asset, _cache))!;

        Assert.Equal(700, waveform.SampleCount);                         // [0, 700): the stream's end is sample 700
        Assert.All(waveform.Peaks.ToArray(), p => Assert.Equal(26, p));  // ⌈0.1·255⌉, never the loud preroll
    }

    [Fact]
    public async Task A_stream_starting_later_leaves_silence_before_it()
    {
        var asset = Media(new FakeAudioSource(PerPeak, StreamStartSample: 3 * PerPeak - 10, Constant: 0.5f));

        var waveform = (await Service().GetOrCreateAsync(asset, _cache))!;

        Assert.Equal(4L * PerPeak - 10, waveform.SampleCount);
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, waveform.Peaks.ToArray());
    }

    [Fact]
    public async Task Audio_without_samples_is_an_empty_waveform()
    {
        var asset = Media(new FakeAudioSource(0));

        var waveform = (await Service().GetOrCreateAsync(asset, _cache))!;

        Assert.Equal(0, waveform.SampleCount);
        Assert.Empty(waveform.Peaks.ToArray());
        Assert.Single(CacheFiles());
    }

    // --- which media --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Video_with_sound_and_audio_files_get_a_waveform()
    {
        Assert.NotNull(await Service().GetOrCreateAsync(Media(kind: MediaKind.Video), _cache));
        Assert.NotNull(await Service().GetOrCreateAsync(Media(kind: MediaKind.Audio, audioCodec: null), _cache));
        Assert.Equal(2, _decoder.Requests.Count);
    }

    [Fact]
    public async Task Media_without_sound_or_analysis_get_none_and_nothing_is_decoded()
    {
        var silentVideo = Media(kind: MediaKind.Video, audioCodec: null);
        var image = Media(kind: MediaKind.Image);
        var pending = Media();
        pending.AnalysisStatus = MediaAnalysisStatus.Pending;
        var failed = Media();
        failed.AnalysisStatus = MediaAnalysisStatus.Failed;
        var noMetadata = Media();
        noMetadata.Metadata = null;

        foreach (var asset in new[] { silentVideo, image, pending, failed, noMetadata })
            Assert.Null(await Service().GetOrCreateAsync(asset, _cache));

        Assert.Empty(_decoder.Requests);
        Assert.Empty(CacheFiles());
    }

    // --- cache: hit, miss, invalidation ---------------------------------------------------------------------------

    [Fact]
    public async Task A_miss_decodes_and_caches_then_every_later_request_is_a_hit_without_decoding()
    {
        var asset = Media();

        var made = (await Service().GetOrCreateAsync(asset, _cache))!;

        var file = Assert.Single(CacheFiles());
        Assert.StartsWith($"{asset.Id:N}-", Path.GetFileName(file));
        Assert.EndsWith("-v1.peaks", file);

        var fresh = Service();                                            // e.g. the project reopened
        var hit = (await fresh.GetOrCreateAsync(asset, _cache))!;
        var cached = fresh.TryGetCached(asset, _cache)!;
        Assert.Equal(made.SampleCount, hit.SampleCount);
        Assert.Equal(made.Peaks.ToArray(), hit.Peaks.ToArray());
        Assert.Equal(made.Peaks.ToArray(), cached.Peaks.ToArray());
        Assert.Single(_decoder.Requests);                                // hits never decode
    }

    [Fact]
    public void TryGetCached_never_decodes()
    {
        var asset = Media();

        Assert.Null(Service().TryGetCached(asset, _cache));

        Assert.Empty(_decoder.Requests);
    }

    [Fact]
    public async Task A_changed_file_size_is_a_miss_and_replaces_the_old_waveform()
    {
        var asset = Media();
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
        var asset = Media();
        await Service().GetOrCreateAsync(asset, _cache);

        File.SetLastWriteTimeUtc(asset.FilePath, File.GetLastWriteTimeUtc(asset.FilePath).AddSeconds(-30)); // same size
        Assert.Null(Service().TryGetCached(asset, _cache));
        await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(2, _decoder.Requests.Count);
        Assert.Single(CacheFiles());
    }

    [Fact]
    public async Task Another_rule_version_is_a_miss_and_replaces_the_old_waveform()
    {
        var asset = Media();
        await Service(ruleVersion: 1).GetOrCreateAsync(asset, _cache);

        await Service(ruleVersion: 2).GetOrCreateAsync(asset, _cache);

        Assert.Equal(2, _decoder.Requests.Count);
        Assert.EndsWith("-v2.peaks", Assert.Single(CacheFiles()));
    }

    [Fact]
    public async Task Thumbnails_and_other_assets_in_the_folder_are_left_alone()
    {
        var asset = Media();
        var other = Media();
        Directory.CreateDirectory(_cache);
        var thumbnail = Path.Combine(_cache, $"{asset.Id:N}-1-1-v1.thumb");
        File.WriteAllText(thumbnail, "thumbnail");
        await Service().GetOrCreateAsync(other, _cache);
        await Service().GetOrCreateAsync(asset, _cache);

        await Service(ruleVersion: 2).GetOrCreateAsync(asset, _cache);   // replaces the asset's v1 waveform only

        Assert.True(File.Exists(thumbnail));
        Assert.Equal(3, CacheFiles().Length);
    }

    // --- damaged cache files ------------------------------------------------------------------------------------

    public static TheoryData<string> Damages => new()
    {
        "empty", "short header", "wrong magic", "wrong version", "zero samples per peak", "negative count",
        "peaks missing", "peaks too many", "huge count"
    };

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task A_damaged_cache_file_is_a_silent_miss_and_is_repaired(string damage)
    {
        var asset = Media();
        await Service().GetOrCreateAsync(asset, _cache);
        var file = Assert.Single(CacheFiles());
        var good = File.ReadAllBytes(file);
        File.WriteAllBytes(file, Damage(good, damage));

        Assert.Null(Service().TryGetCached(asset, _cache));
        var repaired = await Service().GetOrCreateAsync(asset, _cache);

        Assert.NotNull(repaired);
        Assert.Equal(2, _decoder.Requests.Count);
        Assert.Equal(good, File.ReadAllBytes(Assert.Single(CacheFiles())));
    }

    private static byte[] Damage(byte[] good, string damage)
    {
        var bytes = (byte[])good.Clone();
        switch (damage)
        {
            case "empty": return Array.Empty<byte>();
            case "short header": return bytes[..10];
            case "wrong magic": bytes[0] = (byte)'X'; return bytes;
            case "wrong version": BitConverter.GetBytes(99).CopyTo(bytes, 4); return bytes;
            case "zero samples per peak": BitConverter.GetBytes(0).CopyTo(bytes, 8); return bytes;
            case "negative count": BitConverter.GetBytes(-1L).CopyTo(bytes, 12); return bytes;
            case "peaks missing": return bytes[..^1];
            case "peaks too many": return [.. bytes, 7];
            case "huge count": BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 12); return bytes;
            default: throw new ArgumentException(damage);
        }
    }

    // --- offline media ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Offline_media_with_a_cache_shows_it_and_is_never_decoded()
    {
        var asset = Media();
        var made = (await Service().GetOrCreateAsync(asset, _cache))!;

        asset.IsMissing = true;
        var cached = await Service().GetOrCreateAsync(asset, _cache);

        Assert.Equal(made.Peaks.ToArray(), cached!.Peaks.ToArray());
        Assert.Equal(made.Peaks.ToArray(), Service().TryGetCached(asset, _cache)!.Peaks.ToArray());
        Assert.Single(_decoder.Requests);
    }

    [Fact]
    public async Task A_file_gone_during_the_session_is_offline_too()
    {
        var asset = Media();
        await Service().GetOrCreateAsync(asset, _cache);

        File.Delete(asset.FilePath);

        Assert.NotNull(await Service().GetOrCreateAsync(asset, _cache));
        Assert.Single(_decoder.Requests);
    }

    [Fact]
    public async Task Offline_media_without_a_cache_gets_none_and_is_not_decoded()
    {
        var asset = Media();
        asset.IsMissing = true;

        Assert.Null(await Service().GetOrCreateAsync(asset, _cache));

        Assert.Empty(_decoder.Requests);
    }

    // --- failures and cancellation --------------------------------------------------------------------------------

    [Fact]
    public async Task Audio_that_cannot_be_decoded_gets_none_and_nothing_is_cached()
    {
        var asset = Media();
        _decoder.Fail(asset.FilePath);

        Assert.Null(await Service().GetOrCreateAsync(asset, _cache));

        Assert.Empty(CacheFiles());
    }

    [Fact]
    public async Task A_decode_failing_midway_caches_no_truncated_waveform()
    {
        var asset = Media(new FakeAudioSource(100_000, Constant: 0.5f));
        _decoder.FailAfter(asset.FilePath, 50_000);

        Assert.Null(await Service().GetOrCreateAsync(asset, _cache));

        Assert.Empty(CacheFiles());
        Assert.Equal(0, _decoder.LiveStreams);
    }

    [Fact]
    public async Task Cancellation_throws_and_caches_nothing()
    {
        var asset = Media();
        var gate = _decoder.Gate(asset.FilePath);
        using var cts = new CancellationTokenSource();

        var work = Service().GetOrCreateAsync(asset, _cache, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Empty(CacheFiles());
        gate.TrySetResult();
    }

    [Fact]
    public async Task An_unwritable_cache_still_gives_the_waveform()
    {
        var asset = Media();
        var blocked = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocked, "a file where the cache folder should be");

        var waveform = await Service().GetOrCreateAsync(asset, blocked);

        Assert.NotNull(waveform);
        Assert.Single(_decoder.Requests);
    }
}
