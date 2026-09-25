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
public sealed class ExportServiceTests
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

    private ExportService Service(IFfmpegLocator? locator = null, Func<ICompositionRasterizer>? factory = null) =>
        new(locator ?? new Locator(@"C:\ffmpeg.exe"), _video, _audio, _encoder, factory ?? NewRasterizer, NullLogger<ExportService>.Instance);

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
        Assert.DoesNotContain(new ExportProgress(ExportStage.Finalizing, 1, 1), _progress.Reports);
    }

    [Fact]
    public async Task Runs_preparing_audio_video_finalizing_with_exactly_the_output()
    {
        var job = new ExportJob(Snapshot(), Destination);
        await Service().ExportAsync(job, _progress);

        var encoding = _encoder.Encoding!;
        Assert.Equal((job.Output, Destination), (_encoder.Output, _encoder.Destination));
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
        Assert.True(rasterizer.OnlyPoolThreads);
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
        Assert.Equal(new ExportProgress(ExportStage.Video, 0, 50), reports.First(r => r.Stage == ExportStage.Video));
        Assert.Equal(51, reports.Count(r => r.Stage == ExportStage.Video));                    // 0 and after every frame
        Assert.Equal(new ExportProgress(ExportStage.Finalizing, 1, 1), reports[^1]);
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
        await WaitUntil(() => _progress.Reports.Contains(new ExportProgress(ExportStage.Finalizing, 0, 1)));

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
        public bool OnlyPoolThreads { get; private set; } = true;
        public bool Disposed { get; private set; }
        public (int Call, Exception Error)? FailAt { get; set; }

        public void Render(CompositionDrawPlan plan, Span<byte> target, int stride)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (FailAt is { } f && _calls == f.Call) throw f.Error;
            OnlyPoolThreads &= Thread.CurrentThread.IsThreadPoolThread;
            Plans.Add(plan);
            Strides.Add(stride);
            target.Clear();
            BitConverter.TryWriteBytes(target, ++_calls);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEncoder : IExportEncoder
    {
        public ExportOutput? Output { get; private set; }
        public string? Destination { get; private set; }
        public FakeEncoding? Encoding { get; private set; }
        public (string At, Exception Error)? FailAt { get; set; }
        public bool CompleteWaitsForCancellation { get; set; }
        public string? BlockAt { get; set; }

        public Task<IExportEncoding> StartAsync(ExportOutput output, string destinationPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailAt is ("start", var error)) throw error;
            Output = output;
            Destination = destinationPath;
            Encoding = new FakeEncoding(this, output);
            return Task.FromResult<IExportEncoding>(Encoding);
        }

        public sealed class FakeEncoding(FakeEncoder owner, ExportOutput output) : IExportEncoding
        {
            private readonly List<string> _phases = new();
            public List<float> Audio { get; } = new();
            public List<int> FrameMarkers { get; } = new();
            public List<(int Stride, int Length)> Strides { get; } = new();
            public bool Completed { get; private set; }
            public bool Disposed { get; private set; }
            public volatile bool Blocked;
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
                if (owner.BlockAt == "frame" && FrameMarkers.Count == 3) await BlockAsync(ct);
                _phases.Add("video");
                FrameMarkers.Add(BitConverter.ToInt32(bgra.Span));
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
