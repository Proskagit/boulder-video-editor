using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using Xunit;

namespace AiVideoEditor.Export.Tests;

/// <summary>
/// D025 §4 as changed in Phase 28 (the product owner's policy, D044): a Dissolve or a Push whose zone needs source frames
/// beyond its clips that the media do not have refuses the export — found by the decoder before the encoder starts (no
/// encoding at all), or while rendering when the decoder runs out (the encoding is abandoned, never completed). The
/// failure names the transition. Dip to black, Flash and Zoom take no such frames and export as before; a clip's own
/// frames keep D009's hold-last; a range export checks only the zones it exports.
/// </summary>
public sealed partial class ExportServiceTests
{
    private const string FileA = @"C:\media\attemptA.mp4";
    private const string FileB = @"C:\media\attemptB.mp4";
    private const string FileShortA = @"C:\media\attemptA-short.mp4";

    private readonly Guid _track = Guid.NewGuid();
    private readonly Guid _clipA = Guid.NewGuid();
    private readonly Guid _clipB = Guid.NewGuid();

    /// <summary>A [0, 40) of <see cref="FileA"/> (SourceIn 0) | B [40, 80) of <see cref="FileB"/> (SourceIn
    /// <paramref name="bSourceIn"/>) with a 10-frame <paramref name="type"/> on the cut: zone [35, 45).</summary>
    private PlaybackSnapshot Cut(string type, int aFrames = 100, long bSourceIn = 20, int bFrames = 100)
    {
        _video.Add(FileA, new FakeSource(Rate, aFrames));
        _video.Add(FileB, new FakeSource(Rate, bFrames));
        var (assetA, assetB) = (Guid.NewGuid(), Guid.NewGuid());
        var zone = new DissolveZone(_clipA, _clipB, F(35), F(40), F(45), 10)
        {
            TypeId = type,
            Direction = type == TransitionRules.Push ? TransitionDirection.FromRight : null
        };
        var extends = zone.ExtendsPictures;
        var a = new PictureSpan(_clipA, assetA, SpanStatus.Video, F(0), F(40), MediaTime.Zero)
        {
            SourceSize = new FrameSize(1, 1), ExtendedEnd = extends ? F(45) : null
        };
        var b = new PictureSpan(_clipB, assetB, SpanStatus.Video, F(40), F(80), F(bSourceIn))
        {
            SourceSize = new FrameSize(1, 1), ExtendedStart = extends ? F(35) : null
        };
        return new PlaybackSnapshot(1, Rate, F(80),
            ImmutableArray.Create(new VideoLayer(_track, ImmutableArray.Create(a, b)) { Dissolves = ImmutableArray.Create(zone) }),
            ImmutableArray<AudioSpan>.Empty,
            ImmutableDictionary<Guid, PlaybackAsset>.Empty
                .Add(assetA, new PlaybackAsset(assetA, FileA, MediaKind.Video, MediaTime.Zero, Rate))
                .Add(assetB, new PlaybackAsset(assetB, FileB, MediaKind.Video, MediaTime.Zero, Rate)),
            Canvas);
    }

    private void AssertMissing(ExportException error, string type, bool beforeRendering)
    {
        Assert.Equal(ExportFailure.TransitionSourceMissing, error.Failure);
        Assert.Equal(new MissingTransitionSource(type, _track, _clipA, _clipB, F(40), beforeRendering), error.MissingSource);
    }

    [Theory]
    [InlineData(TransitionRules.CrossDissolve)]
    [InlineData(TransitionRules.Push)]
    public async Task A_transition_with_its_handles_exports_every_zone_frame_from_the_source(string type)
    {
        var job = new ExportJob(Cut(type), Destination);

        await Service().ExportAsync(job, _progress);

        Assert.True(_encoder.Encoding!.Completed);
        AssertEverythingReleased();
    }

    [Theory]
    [InlineData(TransitionRules.CrossDissolve)]
    [InlineData(TransitionRules.Push)]
    public async Task B_without_frames_before_its_start_refuses_the_export_before_the_encoder_starts(string type)
    {
        var job = new ExportJob(Cut(type, bSourceIn: 0), Destination);           // B's source starts at its own start

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(job, _progress));

        AssertMissing(error, type, beforeRendering: true);
        Assert.Null(_encoder.Encoding);                                          // nothing was ever written
        Assert.Empty(_rasterizers);
        AssertEverythingReleased();
    }

    [Theory]
    [InlineData(TransitionRules.CrossDissolve)]
    [InlineData(TransitionRules.Push)]
    public async Task A_without_frames_after_its_end_refuses_the_export_before_the_encoder_starts(string type)
    {
        var job = new ExportJob(Cut(type, aFrames: 40), Destination);            // A's source ends where A ends

        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(job, _progress));

        AssertMissing(error, type, beforeRendering: true);
        Assert.Null(_encoder.Encoding);
        AssertEverythingReleased();
    }

    [Fact]
    public async Task One_frame_too_few_is_refused_and_the_exact_handle_is_not()
    {
        // A's zone part needs source frames 40..44: 45 frames are exactly enough, 44 are one too few.
        var enough = new ExportJob(Cut(TransitionRules.CrossDissolve, aFrames: 45), Destination);
        await Service().ExportAsync(enough, _progress);
        Assert.True(_encoder.Encoding!.Completed);

        var tooFew = new ExportJob(Cut(TransitionRules.CrossDissolve, aFrames: 44), Destination);
        var error = await Assert.ThrowsAsync<ExportException>(() => Service().ExportAsync(tooFew, _progress));
        AssertMissing(error, TransitionRules.CrossDissolve, beforeRendering: true);
    }

    [Theory]
    [InlineData(TransitionRules.DipToBlack)]
    [InlineData(TransitionRules.Flash)]
    [InlineData(TransitionRules.Zoom)]
    public async Task Types_that_take_no_frames_beyond_their_clips_export_without_handles_as_before(string type)
    {
        var job = new ExportJob(Cut(type, aFrames: 40, bSourceIn: 0), Destination);

        await Service().ExportAsync(job, _progress);

        Assert.True(_encoder.Encoding!.Completed);
        AssertEverythingReleased();
    }

    [Fact]
    public async Task A_range_export_checks_only_the_zones_it_exports()
    {
        var snapshot = Cut(TransitionRules.Push, bSourceIn: 0);

        await Service().ExportAsync(new ExportJob(snapshot, Destination, range: new ExportRange(50, 80)), _progress);   // after the zone
        Assert.True(_encoder.Encoding!.Completed);

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service().ExportAsync(new ExportJob(snapshot, Destination, range: new ExportRange(37, 60)), _progress));
        AssertMissing(error, TransitionRules.Push, beforeRendering: true);
    }

    [Fact]
    public async Task A_decoder_that_runs_out_while_rendering_stops_the_export_and_completes_nothing()
    {
        // The check before rendering sees the full file; the export's own reader then gets one that ends at A's end —
        // a file whose real frames are fewer than the first decode promised.
        _video.Add(FileShortA, new FakeSource(Rate, 40));
        var job = new ExportJob(Cut(TransitionRules.CrossDissolve), Destination);
        var opens = 0;
        var decoder = new RedirectingDecoder(_video, r =>
            r.FilePath == FileA && Interlocked.Increment(ref opens) > 1 ? r with { FilePath = FileShortA } : r);
        var service = new ExportService(new Locator(@"C:\ffmpeg.exe"), decoder, _audio, _encoder, NewRasterizer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExportService>.Instance);

        var error = await Assert.ThrowsAsync<ExportException>(() => service.ExportAsync(job, _progress));

        AssertMissing(error, TransitionRules.CrossDissolve, beforeRendering: false);
        AssertAbandoned();                                                       // started, never completed, disposed
        Assert.NotNull(_encoder.Encoding);
    }

    [Fact]
    public async Task The_frame_source_fails_on_a_zone_frame_it_could_only_hold()
    {
        await using var source = new ExportFrameSource(Cut(TransitionRules.Push, bSourceIn: 0), _video);
        for (long n = 0; n < 35; n++) await source.GetFrameAsync(n);

        var error = await Assert.ThrowsAsync<ExportException>(() => source.GetFrameAsync(35));

        AssertMissing(error, TransitionRules.Push, beforeRendering: false);
    }

    [Fact]
    public async Task A_clips_own_frames_past_its_source_are_still_held()
    {
        // D009 for the clip's own range: a 30-frame source under a 50-frame clip shows its last frame — no failure.
        _video.Add(VideoFile, new FakeSource(Rate, 30));
        var job = new ExportJob(Snapshot(), Destination);

        await Service().ExportAsync(job, _progress);

        Assert.True(_encoder.Encoding!.Completed);
        var rasterizer = Assert.Single(_rasterizers);
        Assert.Equal(29, FakeVideoDecoder.Number(Assert.IsType<FrameDraw>(Assert.Single(rasterizer.Plans[49].Operations)).Frame));
    }

    /// <summary>A decoder that changes the request before the fake sees it (a file that is not what the first decode found).</summary>
    private sealed class RedirectingDecoder(IVideoDecoder inner, Func<VideoDecodeRequest, VideoDecodeRequest> redirect) : IVideoDecoder
    {
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) => inner.OpenAsync(redirect(request), ct);
    }
}
