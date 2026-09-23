using System.Diagnostics;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Infrastructure;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// Real ffmpeg audio decoding: a click at a known source sample must land at that sample after
/// FirstSampleIndex alignment, whatever the seek position, codec, sample rate or start time.
/// </summary>
[Collection(MediaCollection.Name)]
public sealed class FfmpegAudioDecoderIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-audio-tests", Guid.NewGuid().ToString("N"));
    private readonly TestMedia _media;
    private readonly ITestOutputHelper _output;

    public FfmpegAudioDecoderIntegrationTests(TestMedia media, ITestOutputHelper output)
    {
        _media = media;
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static FfmpegAudioDecoder NewDecoder(FfmpegAudioDecoderSettings? settings = null) =>
        new(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance, settings);

    /// <summary>Generates a mono click at source sample <paramref name="clickAt"/> (at <paramref name="rate"/>).</summary>
    private string Click(string name, int rate, long clickAt, string codecArgs, string? remux = null)
    {
        var path = Path.Combine(_dir, name);
        var source = remux is null ? path : Path.Combine(_dir, "src-" + Path.GetFileNameWithoutExtension(name) + ".m4a");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            $@"aevalsrc=exprs='if(eq(n\,{clickAt})\,0.9\,0)':s={rate}:d=5" };
        args.AddRange(codecArgs.Split(' '));
        args.Add(source);
        TestMedia.Run(FfmpegTools.Ffmpeg!, args);
        if (remux is not null)
            TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", source, "-c", "copy", path });
        return path;
    }

    private static MediaMetadata Analyze(string path)
    {
        var service = new FfprobeMediaAnalysisService(
            new FfprobeLocator(Options.Create(new FfmpegOptions()), NullLogger<FfprobeLocator>.Instance),
            NullLogger<FfprobeMediaAnalysisService>.Instance);
        return service.AnalyzeAsync(path).GetAwaiter().GetResult().Metadata!;
    }

    /// <summary>Decodes from <paramref name="seconds"/> and returns the source sample index of the
    /// loudest frame (48 kHz, from the file's start time), plus the stream.</summary>
    private static async Task<(long Peak, FfmpegAudioStream Stream, float[] Samples)> DecodePeak(string path, double seconds,
        FfmpegAudioDecoderSettings? settings = null)
    {
        var metadata = Analyze(path);
        var stream = await NewDecoder(settings).OpenStreamAsync(new AudioDecodeRequest
        {
            FilePath = path,
            StartTime = metadata.StartTime ?? MediaTime.Zero,
            SourcePosition = MediaTime.FromSeconds(seconds)
        }, CancellationToken.None);

        var all = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());

        var samples = all.ToArray();
        var peak = 0;
        for (var i = 0; i < samples.Length / 2; i++)
            if (Math.Abs(samples[2 * i]) > Math.Abs(samples[2 * peak])) peak = i;
        return (stream.FirstSampleIndex + peak, stream, samples);
    }

    [FfmpegTheory]
    [InlineData(1.0)]
    [InlineData(1.37123)]
    [InlineData(0.0)]
    public async Task Wav48k_ClickLandsOnItsExactSample(double seekSeconds)
    {
        var path = Click("click48.wav", 48_000, 72_000, "-c:a pcm_s16le");
        var (peak, stream, samples) = await DecodePeak(path, seekSeconds);
        await using var _ = stream;

        Assert.Equal(72_000, peak);
        Assert.True(stream.FirstSampleIndex <= AudioTiming.NearestSample(MediaTime.FromSeconds(seekSeconds)));
        var i = (int)(peak - stream.FirstSampleIndex);
        Assert.Equal(samples[2 * i], samples[2 * i + 1]); // mono → both channels
    }

    [FfmpegFact]
    public async Task Wav44k1_IsResampledTo48k_ClickWithinOneSample()
    {
        // 1.5 s at 44.1 kHz = sample 66150 → 72000 at 48 kHz.
        var path = Click("click441.wav", 44_100, 66_150, "-c:a pcm_s16le");
        var (peak, stream, _) = await DecodePeak(path, 1.0);
        await using var _ = stream;
        Assert.InRange(peak, 71_999, 72_001);
    }

    [FfmpegTheory]
    [InlineData(1.0)]
    [InlineData(1.43)]
    public async Task AacInMp4_StartsLateOnSeek_IsRealignedByItsRealFirstSample(double seekSeconds)
    {
        // ffmpeg starts AAC-in-MP4 at a codec frame after -ss; the decoder must detect it and
        // extend the preroll, so the click still lands on its sample (± AAC smearing).
        var path = Click("click.m4a", 48_000, 72_000, "-c:a aac -b:a 192k");
        var (peak, stream, _) = await DecodePeak(path, seekSeconds);
        await using var _ = stream;

        _output.WriteLine($"first sample {stream.FirstSampleIndex}, attempts {stream.Attempts}, peak {peak}");
        Assert.True(stream.FirstSampleIndex <= AudioTiming.NearestSample(MediaTime.FromSeconds(seekSeconds)));
        Assert.InRange(peak, 71_998, 72_002);
        Assert.Equal(PlainDecodePeak(path), peak);
    }

    [FfmpegFact]
    public async Task AacInMp4_WithTooSmallPreroll_RetriesUntilTheStreamStartsInTime()
    {
        // A 1 ms preroll: -ss lands on the next AAC frame boundary, after the requested sample.
        var path = Click("retry.m4a", 48_000, 150_000, "-c:a aac -b:a 192k");
        var settings = new FfmpegAudioDecoderSettings { InitialPreroll = TimeSpan.FromMilliseconds(1) };
        var (peak, stream, _) = await DecodePeak(path, 3.0, settings);
        await using var _ = stream;

        _output.WriteLine($"first sample {stream.FirstSampleIndex}, attempts {stream.Attempts}");
        Assert.True(stream.Attempts > 1, "the first attempt must have started too late");
        Assert.True(stream.FirstSampleIndex <= 144_000);
        Assert.Equal(PlainDecodePeak(path), peak);
    }

    [FfmpegFact]
    public async Task MpegTs_NonZeroStartTime_IsTheOrigin()
    {
        // Remuxing AAC from MP4 to TS keeps the encoder's priming frame (1024 samples) at the
        // container start instead of skipping it, so the click really sits 1024 samples later in
        // the TS. The oracle is ffmpeg's own plain decode of the same file from its start
        // (timestamps rebased to start_time = 0); seek + -copyts + start_time must agree with it.
        var path = Click("click.ts", 48_000, 72_000, "-c:a aac -b:a 192k", remux: "ts");
        var metadata = Analyze(path);
        Assert.True(metadata.StartTime > MediaTime.FromSeconds(1), $"start_time {metadata.StartTime}");
        var reference = PlainDecodePeak(path);
        _output.WriteLine($"plain decode peak {reference}");

        foreach (var seek in new[] { 0.0, 1.0, 1.43 })
        {
            var (peak, stream, _) = await DecodePeak(path, seek);
            await using var _ = stream;
            Assert.InRange(peak, reference - 2, reference + 2);
        }
    }

    /// <summary>Peak sample of ffmpeg's plain decode from the start (no seek, no -copyts).</summary>
    private long PlainDecodePeak(string path)
    {
        var raw = Path.Combine(_dir, Path.GetFileName(path) + ".raw");
        TestMedia.Run(FfmpegTools.Ffmpeg!, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", path, "-map", "0:a:0",
            "-af", "aresample=48000,aformat=sample_fmts=flt:channel_layouts=mono", "-f", "f32le", raw });
        var bytes = File.ReadAllBytes(raw);
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        var peak = 0;
        for (var i = 1; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > Math.Abs(samples[peak])) peak = i;
        return peak;
    }

    [FfmpegFact]
    public async Task FileWithoutAudio_AndMissingFile_FailWithControlledErrors()
    {
        var video = _media.Get("cfr25.mp4");
        var noAudio = await Assert.ThrowsAsync<AudioDecodeException>(() => NewDecoder().OpenAsync(
            new AudioDecodeRequest { FilePath = video.Path, SourcePosition = MediaTime.FromSeconds(1) }));
        Assert.Equal(VideoDecodeError.DecoderFailed, noAudio.Error);

        var missing = await Assert.ThrowsAsync<AudioDecodeException>(() => NewDecoder().OpenAsync(
            new AudioDecodeRequest { FilePath = Path.Combine(_dir, "none.wav") }));
        Assert.Equal(VideoDecodeError.FileNotFound, missing.Error);
    }

    [FfmpegFact]
    public async Task AvSync_ClickIsHeardWhileItsVideoFrameIsShown()
    {
        // avsync.mp4: video frame numbers + an AAC click at 2.02 s, the middle of video frame 50.
        // Played through PlaybackService with a capturing fake device as the master clock.
        var file = _media.Get("avsync.mp4");
        Assert.NotNull(file.Metadata.AudioCodec);
        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, IsFrameRateLocked = true } };
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        var asset = new MediaAsset { FilePath = file.Path, Kind = MediaKind.Video, Metadata = file.Metadata };
        project.MediaAssets.Add(asset);
        var duration = MediaTime.FromFrame(100, FrameRate.Fps25);
        project.Timeline.VideoTracks[0].Clips.Add(new VideoClip { MediaAssetId = asset.Id, Duration = duration, SourceOut = duration });

        var output = new FakeAudioOutput();
        await using var service = new PlaybackService(
            new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance),
            new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, NewDecoder(), output);
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, 1));
        await service.SeekAsync(MediaTime.FromSeconds(1.8));
        service.Play();

        const int block = 240; // 5 ms
        long? clickSample = null;
        long? frameAtClick = null;
        for (var step = 0; step < 100 && clickSample is null; step++)
        {
            var from = AudioTiming.NearestSample(service.Position);
            await WaitFor(() => { service.Update(); return service.AudioHasData(from, from + block); });
            var audio = output.Play(block);
            for (var i = 0; i < block; i++)
            {
                if (Math.Abs(audio[2 * i]) > 0.3f) { clickSample = from + i; break; }
            }
            if (clickSample is not null)
            {
                var frame = await WaitForFrame(service);
                frameAtClick = FfmpegVideoDecoderIntegrationTests.ReadNumber(frame.Picture!.Frame!);
            }
        }

        _output.WriteLine($"click at timeline sample {clickSample} ({clickSample / 48_000.0:0.#####} s), video frame {frameAtClick}");
        Assert.NotNull(clickSample);
        Assert.InRange(clickSample!.Value, 96_960 - 240, 96_960 + 240); // within one 5 ms block
        Assert.Equal(50, frameAtClick);
    }

    [FfmpegFact]
    public async Task PlaybackLifecycle_WithRealDecoders_LeavesNoFfmpegProcess()
    {
        // Video + audio of two clips: play, seek back/forth, pause/play, edits, end, dispose.
        var file = _media.Get("avsync.mp4");
        var project = new Project { Settings = { FrameRate = FrameRate.Fps25, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        var asset = new MediaAsset { FilePath = file.Path, Kind = MediaKind.Video, Metadata = file.Metadata };
        project.MediaAssets.Add(asset);
        var clipLength = MediaTime.FromFrame(50, FrameRate.Fps25);
        v1.Clips.Add(new VideoClip { MediaAssetId = asset.Id, Duration = clipLength, SourceOut = clipLength });
        var second = new VideoClip { MediaAssetId = asset.Id, TimelineStart = clipLength, Duration = clipLength,
            SourceIn = clipLength, SourceOut = clipLength + clipLength };
        v1.Clips.Add(second);

        var baseline = FfmpegProcess.LiveProcesses;
        var output = new FakeAudioOutput();
        var service = new PlaybackService(
            new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance),
            new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { Hardware = HardwareDecoding.Disabled }, NewDecoder(), output);
        long version = 0;
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, ++version));

        async Task PlayAudio(int frames)
        {
            var from = AudioTiming.NearestSample(service.Position);
            await WaitFor(() => { service.Update(); return service.AudioHasData(from, from + frames); });
            output.Play(frames);
        }

        service.Play();
        await PlayAudio(4_800);
        foreach (var seconds in new[] { 3.0, 0.5, 2.2, 1.0 })
        {
            await service.SeekAsync(MediaTime.FromSeconds(seconds));
            await PlayAudio(2_400);
        }
        service.Pause();
        service.Play();
        await PlayAudio(2_400);
        second.Volume = 0.5;
        service.UpdateSnapshot(PlaybackSnapshotBuilder.Build(project, ++version));
        await PlayAudio(2_400);

        await WaitFor(() => { service.Update(); return FfmpegProcess.LiveProcesses - baseline <= 4; });
        _output.WriteLine($"live ffmpeg processes while playing: {FfmpegProcess.LiveProcesses - baseline}");

        await service.SeekAsync(MediaTime.FromSeconds(3.9));
        output.Play(48_000);                                      // run past the end
        Assert.Equal(PlaybackState.Paused, service.Update().State);

        await service.DisposeAsync();
        Assert.Equal(baseline, FfmpegProcess.LiveProcesses);    // nothing left, immediately after Dispose
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(1);
        }
    }

    private static async Task<PlaybackFrame> WaitForFrame(PlaybackService service)
    {
        PlaybackFrame frame = default;
        await WaitFor(() => { frame = service.Update(); return frame.IsPictureCurrent && !frame.IsBuffering && frame.Picture?.Frame is not null; });
        return frame;
    }
}
