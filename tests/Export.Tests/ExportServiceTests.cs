using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// Phase 8 Step 6 (D023): <see cref="ExportService"/> only orchestrates — preparing (one rasterizer, the encoder),
/// the whole audio from <see cref="ExportAudioSource"/>, then every frame from <see cref="ExportFrameSource"/> through
/// the Core plan and the rasterizer, then <see cref="IExportEncoding.CompleteAsync"/>. Progress is monotonic per
/// stage; cancellation and every failure of a part propagate unchanged and end with every part disposed and the
/// encoding never completed.
/// </summary>
public sealed partial class ExportServiceTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly FrameSize Canvas = new(64, 36);
    private const string VideoFile = @"C:\media\v.mp4";
    private const string AudioFile = @"C:\media\a.wav";
    private const string Destination = @"C:\out\export.mp4";

    private readonly FakeVideoDecoder _video = new();
    private readonly FakeAudioDecoder _audio = new();
    private readonly FakeEncoder _encoder = new();
    private readonly List<FakeRasterizer> _rasterizers = new();
    private readonly RecordingProgress _progress = new();

    public ExportServiceTests()
    {
        _video.Add(VideoFile, new FakeSource(Rate, 200));
        _audio.Add(AudioFile, new FakeAudioSource(480_000));
        _progress.Encoder = _encoder;
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>A video clip over [0, 50) (source = timeline, 1×1 frames numbered like the timeline frame) and an audio
    /// clip over [10, 30), in a 50-frame (2 s) sequence.</summary>
    private static PlaybackSnapshot Snapshot()
    {
        var videoAsset = Guid.NewGuid();
        var audioAsset = Guid.NewGuid();
        var picture = new PictureSpan(Guid.NewGuid(), videoAsset, SpanStatus.Video, F(0), F(50), MediaTime.Zero) { SourceSize = new FrameSize(1, 1) };
        var sound = new AudioSpan(Guid.NewGuid(), audioAsset, SpanStatus.Audio, F(10), F(30), MediaTime.Zero, 1, null, false);
        return new PlaybackSnapshot(1, Rate, F(50),
            ImmutableArray.Create(new VideoLayer(Guid.NewGuid(), ImmutableArray.Create(picture))),
            ImmutableArray.Create(sound),
            ImmutableDictionary<Guid, PlaybackAsset>.Empty
                .Add(videoAsset, new PlaybackAsset(videoAsset, VideoFile, MediaKind.Video, MediaTime.Zero, Rate))
                .Add(audioAsset, new PlaybackAsset(audioAsset, AudioFile, MediaKind.Audio, MediaTime.Zero, null)),
            Canvas);
    }

    /// <summary>One rasterizer unless asked (the sequential case keeps the call numbers of <see cref="FakeRasterizer.FailAt"/>
    /// exact); the D059 E2 pool is tested with <paramref name="rasterizers"/> &gt; 1.</summary>
    private ExportService Service(IFfmpegLocator? locator = null, Func<ICompositionRasterizer>? factory = null, int rasterizers = 1,
        int? queued = null, IVideoDecoder? video = null) =>
        new(locator ?? new Locator(@"C:\ffmpeg.exe"), video ?? _video, _audio, _encoder, factory ?? NewRasterizer, NullLogger<ExportService>.Instance,
            new ExportPipelineSettings { Rasterizers = rasterizers, QueuedFrames = queued });

    private FakeRasterizer NewRasterizer()
    {
        var rasterizer = new FakeRasterizer();
        lock (_rasterizers) _rasterizers.Add(rasterizer);
        return rasterizer;
    }

    private void AssertEverythingReleased()
    {
        Assert.All(_rasterizers, r => Assert.True(r.Disposed));
        Assert.Equal(0, _video.LiveStreams);
        Assert.Equal(0, _audio.LiveStreams);
        if (_encoder.Encoding is { } encoding) Assert.True(encoding.Disposed);
    }

    private void AssertAbandoned()
    {
        AssertEverythingReleased();
        Assert.False(_encoder.Encoding?.Completed ?? false);
        Assert.DoesNotContain(_progress.Reports, r => r is { Stage: ExportStage.Finalizing, Done: 1 });
    }

    [Fact]
    public async Task Runs_preparing_audio_video_finalizing_with_exactly_the_output()
    {
        var job = new ExportJob(Snapshot(), Destination);
        await Service().ExportAsync(job, _progress);

        var encoding = _encoder.Encoding!;
        Assert.Equal((job.Output, Destination), (_encoder.Output, _encoder.Destination));
        Assert.Same(ExportEncoding.Default, _encoder.Settings);              // a job without settings: the default (D028 13.7)
        Assert.Equal(new[] { "audio", "video", "complete", "dispose" }, encoding.Phases);
        Assert.True(encoding.Completed);

        // The audio is ExportAudioSource's, sample for sample, and exactly AudioSampleCount frames.
        Assert.Equal(96_000, job.Output.AudioSampleCount);
        var expected = new List<float>();
        await using (var reference = new ExportAudioSource(job.Snapshot, _audio))
        {
            var buffer = new float[2 * 10_000];
            int read;
            while ((read = await reference.ReadAsync(buffer)) > 0) expected.AddRange(buffer.Take(read));
        }
        Assert.Equal(expected, encoding.Audio);

        // Every frame: the frame source's layers → the Core plan → the one rasterizer → that canvas to the encoder.
        var rasterizer = Assert.Single(_rasterizers);
        Assert.Equal(50, rasterizer.Plans.Count);
        Assert.All(rasterizer.Plans, p => Assert.Equal((Canvas, Affine2D.Identity), (p.Canvas, p.CanvasToTarget)));
        Assert.Equal(Enumerable.Range(0, 50), rasterizer.Plans.Select(p => FakeVideoDecoder.Number(Assert.IsType<FrameDraw>(Assert.Single(p.Operations)).Frame)));
        Assert.Equal(Enumerable.Range(1, 50), encoding.FrameMarkers);                 // the canvas the rasterizer just filled
        Assert.All(encoding.Strides, s => Assert.Equal((Canvas.Width * 4, Canvas.Width * 4 * Canvas.Height), s));
        Assert.All(rasterizer.Strides, s => Assert.Equal(Canvas.Width * 4, s));
        Assert.True(rasterizer.OnlyRasterizerThreads);   // D059 E2: the pipeline's own threads, never the caller's
        AssertEverythingReleased();
    }

    [Fact]
    public async Task The_encoder_gets_the_jobs_export_settings_and_nothing_else()
    {
        var settings = new ExportEncoding(ExportQuality.Maximum, ExportSpeedPreset.Slow, 160);
        var job = new ExportJob(Snapshot(), Destination, settings);

        await Service().ExportAsync(job, _progress);

        Assert.Same(settings, _encoder.Settings);
        Assert.Same(job.Encoding, _encoder.Settings);
        AssertEverythingReleased();
    }

    [Fact]
    public async Task Progress_is_monotonic_within_each_stage_and_ends_after_completion()
    {
        var job = new ExportJob(Snapshot(), Destination);
        await Service().ExportAsync(job, _progress);

        var reports = _progress.Reports;
        Assert.Equal(new ExportProgress(ExportStage.Preparing, 0, 1), reports[0]);
        Assert.Equal(new[] { ExportStage.Preparing, ExportStage.Audio, ExportStage.Video, ExportStage.Finalizing },
            reports.Select(r => r.Stage).Distinct());
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Stage >= reports[i - 1].Stage, $"stage went back at {i}");
            if (reports[i].Stage != reports[i - 1].Stage) continue;
            Assert.Equal(reports[i - 1].Total, reports[i].Total);
            Assert.True(reports[i].Done >= reports[i - 1].Done, $"{reports[i]} after {reports[i - 1]}");
        }
        Assert.Equal(new ExportProgress(ExportStage.Audio, 0, 96_000), reports.First(r => r.Stage == ExportStage.Audio));
        Assert.Equal(new ExportProgress(ExportStage.Audio, 96_000, 96_000), reports.Last(r => r.Stage == ExportStage.Audio));
        Assert.Equal(new ExportProgress(ExportStage.Video, 0, 50), reports.First(r => r.Stage == ExportStage.Video) with { Encoder = null });
        Assert.All(reports.Where(r => r.Stage >= ExportStage.Video), r => Assert.Equal("libx264 medium CRF 18", r.Encoder));   // D059 §3
        Assert.All(reports.Where(r => r.Stage < ExportStage.Video), r => Assert.Null(r.Encoder));
        Assert.Equal(51, reports.Count(r => r.Stage == ExportStage.Video));                    // 0 and after every frame
        Assert.Equal(new ExportProgress(ExportStage.Finalizing, 1, 1), reports[^1] with { Encoder = null });
        Assert.Equal(new[] { 0L, 1L }, reports.Where(r => r.Stage == ExportStage.Finalizing).Select(r => r.Done));
        Assert.Equal(1, _progress.ReportsAfterComplete);                                       // only the final 1/1
    }

    [Fact]
    public async Task Runs_off_the_calling_thread()
    {
        using var release = new ManualResetEventSlim();
        // Blocks whoever runs the export (bounded, so running on the caller's thread fails instead of hanging).
        var service = Service(factory: () => { release.Wait(TimeSpan.FromSeconds(5)); return NewRasterizer(); });

        var export = service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress);  // returns while the export blocks

        Assert.False(export.IsCompleted);
        release.Set();
        await export;
    }

    [Fact]
    public async Task One_rasterizer_per_export()
    {
        var service = Service();
        await service.ExportAsync(new ExportJob(Snapshot(), Destination), null);
        await service.ExportAsync(new ExportJob(Snapshot(), Destination), null);

        Assert.Equal(2, _rasterizers.Count);
        Assert.All(_rasterizers, r => Assert.Equal(50, r.Plans.Count));
        Assert.All(_rasterizers, r => Assert.True(r.Disposed));
    }

    [Fact]
    public async Task Cancelled_during_audio()
    {
        var gate = _audio.Gate(AudioFile);                                                     // the audio decoder never opens
        using var cts = new CancellationTokenSource();
        var export = Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _audio.Requests.Count == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Empty(_encoder.Encoding!.FrameMarkers);
        AssertAbandoned();
        gate.TrySetResult();
    }

    [Fact]
    public async Task Cancelled_during_video()
    {
        var release = _video.HoldAfter(VideoFile, frames: 10);                                // frames 0..9, then the decoder stalls
        using var cts = new CancellationTokenSource();
        var export = Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _encoder.Encoding?.FrameMarkers.Count >= 9);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.InRange(_encoder.Encoding!.FrameMarkers.Count, 9, 10);
        AssertAbandoned();
        release.TrySetResult();
    }

    [Fact]
    public async Task Cancelled_during_completion()
    {
        _encoder.CompleteWaitsForCancellation = true;
        using var cts = new CancellationTokenSource();
        var export = Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _progress.Reports.Any(r => r is { Stage: ExportStage.Finalizing, Done: 0 }));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        Assert.Equal(50, _encoder.Encoding!.FrameMarkers.Count);
        AssertAbandoned();
    }

    /// <summary>An encoder that stops reading (ffmpeg stuck) must still be left on cancellation: the token reaches it.</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("frame")]
    public async Task Cancelled_while_the_encoder_blocks(string at)
    {
        _encoder.BlockAt = at;
        using var cts = new CancellationTokenSource();
        var export = Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _encoder.Encoding?.Blocked == true);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertAbandoned();
    }

    [Fact]
    public async Task Cancelled_before_it_starts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token));

        Assert.Null(_encoder.Encoding);
        Assert.Empty(_rasterizers);
    }

    [Fact]
    public async Task A_video_decode_failure_stops_the_export_at_that_frame()
    {
        _video.FailAfter(VideoFile, 11, software: true);                                      // frames 0..10, then the stream breaks

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
        Assert.Equal(10, _encoder.Encoding!.FrameMarkers.Count);                             // frame 10 needs frame 11 to be certain
        AssertAbandoned();
    }

    [Fact]
    public async Task An_audio_decode_failure_stops_the_export_before_any_frame()
    {
        _audio.Fail(AudioFile);

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Equal(ExportFailure.DecodeFailed, error.Failure);
        Assert.Empty(_encoder.Encoding!.FrameMarkers);
        Assert.Empty(_video.Requests);
        AssertAbandoned();
    }

    [Fact]
    public async Task A_decoder_that_needs_the_missing_ffmpeg_keeps_its_category()
    {
        _video.FailOpen(VideoFile, VideoDecodeError.DecoderUnavailable);

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Equal(ExportFailure.EncoderUnavailable, error.Failure);
        AssertAbandoned();
    }

    public static TheoryData<string, ExportFailure> EncoderFailures => new()
    {
        { "start", ExportFailure.EncoderUnavailable },
        { "start", ExportFailure.OutputFailed },
        { "audio", ExportFailure.EncodeFailed },
        { "frame", ExportFailure.EncodeFailed },
        { "complete", ExportFailure.EncodeFailed },
        { "complete", ExportFailure.OutputFailed },
    };

    [Theory]
    [MemberData(nameof(EncoderFailures))]
    public async Task Encoder_failures_propagate_unchanged(string at, ExportFailure failure)
    {
        var thrown = new ExportException(failure, $"{at} failed");
        _encoder.FailAt = (at, thrown);

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Same(thrown, error);
        if (at == "start") Assert.Empty(_audio.Requests);
        AssertAbandoned();
    }

    [Fact]
    public async Task A_rasterizer_failure_propagates_and_abandons_the_encoding()
    {
        var thrown = new InvalidOperationException("render failed");
        var service = Service(factory: () => { var r = NewRasterizer(); r.FailAt = (5, thrown); return r; });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Same(thrown, error);
        Assert.Equal(5, _encoder.Encoding!.FrameMarkers.Count);
        AssertAbandoned();
    }

    // --- Step 9.7 (A): the next frame is fetched while the current one is rasterized and written -------------------

    /// <summary>Counts the frames the export reads from the decoder, and notices a stream disposed while a read on it
    /// is still running (a real ffmpeg stream would be torn down under the reader).</summary>
    private sealed class CountingDecoder(IVideoDecoder inner) : IVideoDecoder
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public volatile bool DisposedWhileReading;

        public async Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            new Stream(await inner.OpenAsync(request, ct), this);

        private sealed class Stream(IVideoFrameStream inner, CountingDecoder owner) : IVideoFrameStream
        {
            private int _reading;

            public async ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                Interlocked.Increment(ref _reading);
                try
                {
                    var frame = await inner.ReadFrameAsync(ct);
                    if (frame is not null) Interlocked.Increment(ref owner._reads);
                    return frame;
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(50, CancellationToken.None);   // a cancelled pipe read takes a moment to unwind
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref _reading);
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Volatile.Read(ref _reading) != 0) owner.DisposedWhileReading = true;
                return inner.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public async Task While_the_encoder_is_stuck_the_frames_fetched_ahead_are_bounded(int rasterizers, int queued)
    {
        // D059 E2 (replaces Step 9.7's "one frame ahead"): the encoder stops at frame 3. Ahead of the writer at most
        // K + 2 canvases (rendered or rendering, the one being written included), the queue and the frame the producer
        // holds — and the decoder reads one source frame beyond the last output frame fetched (the reader's look-ahead).
        _encoder.BlockAt = "frame";
        var counting = new CountingDecoder(_video);
        var service = Service(video: counting, rasterizers: rasterizers, queued: queued);
        using var cts = new CancellationTokenSource();
        var export = service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _encoder.Encoding?.Blocked == true);

        await Task.Delay(300);
        var reads = counting.Reads;
        await Task.Delay(300);
        Assert.Equal(reads, counting.Reads);                                                  // it stopped: bounded
        Assert.Equal(3, _encoder.Encoding!.FrameMarkers.Count);
        var bound = 3 + (rasterizers + 2) + queued + 1 + 1;
        Assert.InRange(reads, 5, bound);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertAbandoned();
    }

    [Fact]
    public async Task A_rasterizer_failure_while_the_next_frame_is_stuck_in_the_decoder_still_releases_everything()
    {
        // Source frames 0..5 come, then the decoder stalls: frame 4 is complete, fetching frame 5 ahead waits for source
        // frame 6. The rasterizer fails on frame 4 — the fetch ahead must be stopped, not waited for forever.
        var release = _video.HoldAfter(VideoFile, frames: 6);
        var thrown = new InvalidOperationException("render failed");
        var counting = new CountingDecoder(_video);
        var service = Service(video: counting, factory: () => { var r = NewRasterizer(); r.FailAt = (4, thrown); return r; });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(thrown, error);
        Assert.Equal(4, _encoder.Encoding!.FrameMarkers.Count);
        Assert.False(counting.DisposedWhileReading);                                          // the fetch ended first
        AssertAbandoned();
        release.TrySetResult();
    }

    // --- D059 E2: several rasterizers -------------------------------------------------------------------------------------

    [Fact]
    public async Task With_several_rasterizers_every_frame_is_drawn_once_and_reaches_the_encoder_in_order()
    {
        var made = 0;
        // Different speeds, so frames finish out of order and the ordering is what puts them back.
        var service = Service(rasterizers: 4, factory: () =>
        {
            var r = NewRasterizer();
            r.Delay = TimeSpan.FromMilliseconds(1 + 4 * (Interlocked.Increment(ref made) % 4));
            return r;
        });

        await service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress);

        Assert.Equal(4, _rasterizers.Count);
        Assert.Equal(50, _rasterizers.Sum(r => r.Plans.Count));                                  // each frame once
        Assert.True(_rasterizers.Count(r => r.Plans.Count > 0) > 1);                               // really in parallel
        Assert.Equal(Enumerable.Range(0, 50), _encoder.Encoding!.SourceNumbers);                    // in frame order
        Assert.Equal(50, _rasterizers.SelectMany(r => r.Plans).Select(p => FakeVideoDecoder.Number(
            Assert.IsType<FrameDraw>(Assert.Single(p.Operations)).Frame)).Distinct().Count());
        Assert.All(_rasterizers, r => Assert.True(r.OnlyRasterizerThreads));
        Assert.Equal(4, _rasterizers.SelectMany(r => r.Threads).Distinct().Count());              // a thread each
        Assert.Equal(51, _progress.Reports.Count(r => r.Stage == ExportStage.Video));
        Assert.Equal(Enumerable.Range(0, 51).Select(n => (long)n), _progress.Reports.Where(r => r.Stage == ExportStage.Video).Select(r => r.Done));
        Assert.True(_encoder.Encoding!.Completed);
        AssertEverythingReleased();
    }

    [Fact]
    public async Task A_failure_in_one_of_several_rasterizers_propagates_and_abandons_everything()
    {
        var thrown = new InvalidOperationException("render failed");
        var made = 0;
        var service = Service(rasterizers: 4, factory: () =>
        {
            var r = NewRasterizer();
            r.Delay = TimeSpan.FromMilliseconds(2);
            if (Interlocked.Increment(ref made) == 3) r.FailAt = (2, thrown);
            return r;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Same(thrown, error);
        Assert.True(_encoder.Encoding!.FrameMarkers.Count < 50);
        Assert.Equal(Enumerable.Range(0, _encoder.Encoding.SourceNumbers.Count), _encoder.Encoding.SourceNumbers);   // no gap, no swap
        AssertAbandoned();
    }

    [Fact]
    public async Task Cancelling_while_several_rasterizers_work_ends_promptly_and_releases_everything()
    {
        using var cts = new CancellationTokenSource();
        var service = Service(rasterizers: 4, factory: () => { var r = NewRasterizer(); r.Delay = TimeSpan.FromMilliseconds(20); return r; });
        var export = service.ExportAsync(new ExportJob(Snapshot(), Destination), _progress, cts.Token);
        await WaitUntil(() => _encoder.Encoding?.FrameMarkers.Count >= 5);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"cancelling took {watch.Elapsed}");
        AssertAbandoned();
    }

    [Theory]
    [InlineData(16, false, 3840, 2160, 1, 5)]     // the target machine (16 threads) with libx264: a third
    [InlineData(16, true, 3840, 2160, 1, 8)]      // with NVENC: half
    [InlineData(8, false, 1920, 1080, 1, 2)]
    [InlineData(4, true, 1920, 1080, 1, 2)]
    [InlineData(2, false, 1920, 1080, 1, 1)]      // never fewer than one
    [InlineData(64, true, 1920, 1080, 1, 8)]      // never more than eight
    public void The_rasterizers_follow_the_processors_and_the_encoder(int processors, bool hardware, int w, int h, int layers, int expected)
    {
        var output = new ExportOutput(new FrameSize(w, h), Rate, 100, MediaTime.FromSeconds(4), 192_000);
        var (k, queue) = new ExportPipelineSettings().Resolve(output, layers, hardware, processors, availableMemoryBytes: 32L << 30);
        Assert.Equal(expected, k);
        Assert.Equal(k, queue);
    }

    [Fact]
    public void The_memory_bounds_the_rasterizers_and_set_values_win()
    {
        var output = new ExportOutput(new FrameSize(3840, 2160), Rate, 100, MediaTime.FromSeconds(4), 192_000);
        // 2 GB available: a quarter is 512 MB; a 4K rasterizer with two layers holds ~7 canvases (~232 MB) → 2.
        Assert.Equal(2, new ExportPipelineSettings().Resolve(output, 2, true, 16, 2L << 30).Rasterizers);
        Assert.Equal(1, new ExportPipelineSettings().Resolve(output, 4, true, 16, 256L << 20).Rasterizers);
        Assert.Equal((3, 7), new ExportPipelineSettings { Rasterizers = 3, QueuedFrames = 7 }.Resolve(output, 1, false, 16, 32L << 30));
        Assert.Equal(ExportPipelineSettings.MaxRasterizers, new ExportPipelineSettings { Rasterizers = 99 }.Resolve(output, 1, false).Rasterizers);
    }

    // --- D059 §3: a hardware encoder that fails --------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_hardware_encoder_is_replaced_by_libx264_of_the_same_profile_and_the_export_succeeds()
    {
        _encoder.HardwareFails = true;
        var nvenc = ExportEncoding.Default with { Profile = ExportProfile.Fast, Encoder = ExportVideoEncoder.Nvenc };

        await Service(rasterizers: 2).ExportAsync(new ExportJob(Snapshot(), Destination, nvenc), _progress);

        Assert.Equal(new[] { nvenc, nvenc.WithLibx264() }, _encoder.Started);
        Assert.True(_encoder.Encoding!.Completed);
        Assert.Equal(Enumerable.Range(0, 50), _encoder.Encoding.SourceNumbers);                    // the whole video again
        var last = _progress.Reports[^1];
        Assert.Equal("libx264 medium CRF 18", last.Encoder);
        Assert.Contains("session could not be opened", last.Fallback);
        Assert.Contains(_progress.Reports, r => r.Encoder == "NVENC p5 CQ 22");
        AssertEverythingReleased();
    }

    [Fact]
    public async Task A_software_encoder_failure_is_not_retried()
    {
        var thrown = new ExportException(ExportFailure.EncodeFailed, "x264 failed");
        _encoder.FailAt = ("frame", thrown);

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(new ExportJob(Snapshot(), Destination), _progress));

        Assert.Same(thrown, error);
        Assert.Single(_encoder.Started);
        AssertAbandoned();
    }

    [Fact]
    public async Task Cancelling_a_hardware_export_is_not_a_fallback()
    {
        _encoder.BlockAt = "frame";
        using var cts = new CancellationTokenSource();
        var nvenc = ExportEncoding.Default with { Encoder = ExportVideoEncoder.Nvenc };
        var export = Service().ExportAsync(new ExportJob(Snapshot(), Destination, nvenc), _progress, cts.Token);
        await WaitUntil(() => _encoder.Encoding?.Blocked == true);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Single(_encoder.Started);
        AssertAbandoned();
    }

    [Fact]
    public async Task Is_available_when_ffmpeg_is_found()
    {
        Assert.True(await Service(new Locator(@"C:\ffmpeg.exe")).IsAvailableAsync());
        Assert.False(await Service(new Locator(null)).IsAvailableAsync());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class Locator(string? path) : IFfmpegLocator
    {
        public Task<string?> GetFfmpegPathAsync(CancellationToken ct = default) => Task.FromResult(path);
    }

    private sealed class RecordingProgress : IProgress<ExportProgress>
    {
        private readonly List<ExportProgress> _reports = new();
        public List<ExportProgress> Reports { get { lock (_reports) return _reports.ToList(); } }
        public int ReportsAfterComplete { get; private set; }
        public FakeEncoder? Encoder { get; set; }

        public void Report(ExportProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
                if (Encoder?.Encoding?.Completed == true) ReportsAfterComplete++;
            }
        }
    }

    /// <summary>Records what it renders; stamps the call number into the canvas (first 4 bytes).</summary>
    private sealed class FakeRasterizer : ICompositionRasterizer
    {
        private int _calls;
        public List<CompositionDrawPlan> Plans { get; } = new();
        public List<int> Strides { get; } = new();
        public bool OnlyRasterizerThreads { get; private set; } = true;
        public HashSet<int> Threads { get; } = new();
        public TimeSpan Delay { get; set; }
        public bool Disposed { get; private set; }
        public (int Call, Exception Error)? FailAt { get; set; }

        public void Render(CompositionDrawPlan plan, Span<byte> target, int stride)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (FailAt is { } f && _calls == f.Call) throw f.Error;
            OnlyRasterizerThreads &= Thread.CurrentThread.Name?.StartsWith("export-rasterizer-", StringComparison.Ordinal) == true;
            Threads.Add(Environment.CurrentManagedThreadId);
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            Plans.Add(plan);
            Strides.Add(stride);
            target.Clear();
            // The source frame number in bytes 4..7 (the parallel tests check the order through it), the call in 0..3.
            BitConverter.TryWriteBytes(target, ++_calls);
            if (plan.Operations.OfType<FrameDraw>().FirstOrDefault() is { } frame)
                BitConverter.TryWriteBytes(target[4..], FakeVideoDecoder.Number(frame.Frame));
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEncoder : IExportEncoder
    {
        public ExportOutput? Output { get; private set; }
        public ExportEncoding? Settings { get; private set; }
        public string? Destination { get; private set; }
        public FakeEncoding? Encoding { get; private set; }
        public (string At, Exception Error)? FailAt { get; set; }
        public bool CompleteWaitsForCancellation { get; set; }
        public string? BlockAt { get; set; }

        /// <summary>D059 §3: an NVENC request is a hardware encoding; with this set, it fails at its fourth frame.</summary>
        public bool HardwareFails { get; set; }

        /// <summary>Every encoding started, in order (a fallback starts a second one).</summary>
        public List<ExportEncoding> Started { get; } = new();

        public Task<IExportEncoding> StartAsync(ExportOutput output, ExportEncoding encoding, string destinationPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailAt is ("start", var error)) throw error;
            Output = output;
            Settings = encoding;
            Destination = destinationPath;
            Started.Add(encoding);
            Encoding = new FakeEncoding(this, output) { IsHardware = encoding.Encoder == ExportVideoEncoder.Nvenc };
            return Task.FromResult<IExportEncoding>(Encoding);
        }

        public sealed class FakeEncoding(FakeEncoder owner, ExportOutput output) : IExportEncoding
        {
            private readonly List<string> _phases = new();
            public List<float> Audio { get; } = new();
            public List<int> FrameMarkers { get; } = new();
            public List<int> SourceNumbers { get; } = new();
            public List<(int Stride, int Length)> Strides { get; } = new();
            public bool Completed { get; private set; }
            public bool Disposed { get; private set; }
            public volatile bool Blocked;
            public bool IsHardware { get; init; }
            public string EncoderDescription => IsHardware ? "NVENC p5 CQ 22" : "libx264 medium CRF 18";
            public IReadOnlyList<string> Phases => _phases.Distinct().ToList();

            public async ValueTask WriteAudioAsync(ReadOnlyMemory<float> interleaved, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.FailAt is ("audio", var error)) throw error;
                if (owner.BlockAt == "audio") await BlockAsync(ct);
                _phases.Add("audio");
                Audio.AddRange(interleaved.ToArray());
            }

            public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> bgra, int stride, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (Audio.Count != output.AudioSampleCount * 2) throw new InvalidOperationException("audio incomplete");
                if (owner.FailAt is ("frame", var error) && FrameMarkers.Count == 3) throw error;
                if (owner.HardwareFails && IsHardware && FrameMarkers.Count == 3)
                    throw new ExportException(ExportFailure.EncodeFailed, "NVENC: the session could not be opened.");
                if (owner.BlockAt == "frame" && FrameMarkers.Count == 3) await BlockAsync(ct);
                _phases.Add("video");
                FrameMarkers.Add(BitConverter.ToInt32(bgra.Span));
                SourceNumbers.Add(BitConverter.ToInt32(bgra.Span[4..]));
                Strides.Add((stride, bgra.Length));
            }

            /// <summary>Stops reading until cancelled, like an ffmpeg that no longer drains its stdin.</summary>
            private async Task BlockAsync(CancellationToken ct)
            {
                Blocked = true;
                await Task.Delay(Timeout.Infinite, ct);
            }

            public async Task CompleteAsync(CancellationToken ct = default)
            {
                if (FrameMarkers.Count != output.FrameCount) throw new InvalidOperationException("frames missing");
                if (owner.FailAt is ("complete", var error)) throw error;
                if (owner.CompleteWaitsForCancellation) await Task.Delay(Timeout.Infinite, ct);
                _phases.Add("complete");
                Completed = true;
            }

            public ValueTask DisposeAsync()
            {
                _phases.Add("dispose");
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
