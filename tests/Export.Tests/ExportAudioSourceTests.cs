using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// Phase 8 Step 4 (D023): the export's audio reader is offline and blocking — never realtime semantics (no
/// underrun silence, no stale samples) — delivers exactly <see cref="ExportOutput.AudioSampleCount"/> frames of
/// 48 kHz stereo float in timeline order, and turns every decoder failure into an <see cref="ExportException"/>.
/// </summary>
public sealed class ExportAudioSourceTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string File = @"C:\media\a.wav";

    private readonly FakeAudioDecoder _decoder = new();

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    /// <summary>One audio clip of <see cref="File"/> over [startFrame, endFrame) of a sequence of
    /// <paramref name="durationFrames"/> frames.</summary>
    private static PlaybackSnapshot Snapshot(long startFrame = 0, long endFrame = 50, long durationFrames = 50,
        SpanStatus status = SpanStatus.Audio, double gain = 1, bool muted = false)
    {
        var asset = Guid.NewGuid();
        var span = new AudioSpan(Guid.NewGuid(), asset, status, F(startFrame), F(endFrame), MediaTime.Zero, gain,
            status == SpanStatus.Audio ? null : "not playable", muted);
        return new PlaybackSnapshot(1, Rate, F(durationFrames), ImmutableArray<VideoLayer>.Empty, ImmutableArray.Create(span),
            ImmutableDictionary<Guid, PlaybackAsset>.Empty.Add(asset, new PlaybackAsset(asset, File, MediaKind.Audio, MediaTime.Zero, null)),
            new FrameSize(1920, 1080));
    }

    private static async Task<float[]> ReadAll(ExportAudioSource source, int chunkFrames = 1000)
    {
        var all = new List<float>();
        var buffer = new float[chunkFrames * 2];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
            all.AddRange(buffer.Take(read));
        return all.ToArray();
    }

    [Fact]
    public async Task Reads_run_sequentially_to_exactly_the_output_sample_count()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        await using var source = new ExportAudioSource(Snapshot(), _decoder);           // 2 s

        var samples = await ReadAll(source, chunkFrames: 7_777);

        Assert.Equal(96_000, source.SampleCount);
        Assert.Equal(96_000 * 2, samples.Length);
        Assert.Equal(96_000, source.Position);
        for (var k = 0; k < 96_000; k += 1234)
            Assert.Equal(k, FakeAudioSource.IndexOf(samples[2 * k]));
        Assert.Equal(0, await source.ReadAsync(new float[16]));                          // at the end
        Assert.Equal(1, _decoder.OpenCount(File));
    }

    [Fact]
    public async Task A_buffer_of_half_a_stereo_frame_is_rejected()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        await using var source = new ExportAudioSource(Snapshot(), _decoder);

        await Assert.ThrowsAsync<ArgumentException>(async () => await source.ReadAsync(new float[3]));
    }

    [Fact]
    public async Task A_slow_decoder_is_waited_for_instead_of_playing_silence()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        var gate = _decoder.Gate(File);
        await using var source = new ExportAudioSource(Snapshot(), _decoder);

        var buffer = new float[2_000];
        var pending = source.ReadAsync(buffer).AsTask();
        await Task.Delay(300);
        Assert.False(pending.IsCompleted);                                               // the Preview would output silence (underrun)

        gate.SetResult();
        Assert.Equal(2_000, await pending);
        Assert.Equal(999, FakeAudioSource.IndexOf(buffer[2 * 999]));
    }

    [Theory]
    [InlineData(VideoDecodeError.DecoderFailed, ExportFailure.DecodeFailed)]
    [InlineData(VideoDecodeError.FileNotFound, ExportFailure.DecodeFailed)]
    [InlineData(VideoDecodeError.DecoderUnavailable, ExportFailure.EncoderUnavailable)]
    public async Task A_decoder_that_cannot_open_fails_the_export(VideoDecodeError error, ExportFailure expected)
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        _decoder.Fail(File, error);
        await using var source = new ExportAudioSource(Snapshot(), _decoder);

        var failure = await Assert.ThrowsAsync<ExportException>(async () => await source.ReadAsync(new float[200]));
        Assert.Equal(expected, failure.Failure);
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_the_stream_fails_the_export_instead_of_going_silent()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        _decoder.FailAfter(File, 30_000);
        await using var source = new ExportAudioSource(Snapshot(), _decoder);

        var failure = await Assert.ThrowsAsync<ExportException>(() => ReadAll(source));
        Assert.Equal(ExportFailure.DecodeFailed, failure.Failure);
        Assert.Contains("a.wav", failure.Message);
    }

    [Fact]
    public async Task A_stream_without_samples_is_silence_like_in_the_preview()
    {
        // ffmpeg ended normally without an audio frame (e.g. the clip lies past the end of the audio): the source has
        // no audio there. A failing ffmpeg is an exception (above).
        _decoder.Add(File, new FakeAudioSource(0));
        await using var source = new ExportAudioSource(Snapshot(), _decoder);

        Assert.All(await ReadAll(source), s => Assert.Equal(0f, s));
    }

    [Theory]
    [InlineData(SpanStatus.Offline)]
    [InlineData(SpanStatus.Unsupported)]
    public async Task Audible_offline_or_unsupported_audio_is_an_error(SpanStatus status)
    {
        await using var source = new ExportAudioSource(Snapshot(status: status), _decoder);

        var failure = await Assert.ThrowsAsync<ExportException>(async () => await source.ReadAsync(new float[200]));
        Assert.Contains("not playable", failure.Message);
        Assert.Empty(_decoder.Requests);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(0.0, false)]
    public async Task Inaudible_clips_are_silence_and_never_decoded_even_when_offline(double gain, bool muted)
    {
        await using var source = new ExportAudioSource(Snapshot(status: SpanStatus.Offline, gain: gain, muted: muted), _decoder);

        Assert.All(await ReadAll(source), s => Assert.Equal(0f, s));
        Assert.Empty(_decoder.Requests);
    }

    [Fact]
    public async Task The_clip_is_decoded_only_while_it_plays_and_released_after_it()
    {
        _decoder.Add(File, new FakeAudioSource(480_000));
        await using var source = new ExportAudioSource(Snapshot(startFrame: 25, endFrame: 50, durationFrames: 75), _decoder);  // [1 s, 2 s) of 3 s

        await source.ReadAsync(new float[2 * 40_000]);
        Assert.Equal(0, _decoder.LiveStreams);                                           // not reached yet
        await source.ReadAsync(new float[2 * 40_000]);
        Assert.Equal(1, _decoder.LiveStreams);                                           // playing
        await source.ReadAsync(new float[2 * 40_000]);
        Assert.Equal(0, _decoder.LiveStreams);                                           // ended at 96 000
        Assert.Equal(0, source.ReaderCount);
    }

    [Fact]
    public async Task Cancellation_stops_the_read_and_dispose_closes_every_stream()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        var source = new ExportAudioSource(Snapshot(), _decoder);
        await source.ReadAsync(new float[200]);
        Assert.Equal(1, _decoder.LiveStreams);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadAsync(new float[200], cts.Token));

        await source.DisposeAsync();
        Assert.Equal(0, _decoder.LiveStreams);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await source.ReadAsync(new float[200]));
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_the_decoder_stops_immediately()
    {
        _decoder.Add(File, new FakeAudioSource(96_000));
        _decoder.Gate(File);
        await using var source = new ExportAudioSource(Snapshot(), _decoder);
        using var cts = new CancellationTokenSource(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadAsync(new float[200], cts.Token));
        Assert.Equal(0, _decoder.LiveStreams);
    }
}
