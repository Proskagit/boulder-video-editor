extern alias ProjectPersistence;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Export;
using ProjectPersistence::AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Video;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D025 §4 as changed in Phase 28 (D044, the product owner's policy) with the real decoders and the ffmpeg encoder: a
/// Dissolve or a Push whose file does not have the frames its zone needs beyond the clip refuses the export — also when
/// the metadata say otherwise (a file replaced by a shorter one): the decoder is asked before rendering, no file is made
/// and an existing destination stays byte for byte. A decoder that runs out while rendering stops the export: the
/// unfinished output is deleted, the destination stays, no ffmpeg is left. A Phase 27 (v7) project with dissolves whose
/// handles suffice exports exactly as the same project built in memory.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTransitionSourceEndToEndTests
{
    private static readonly byte[] Previous = "previous export"u8.ToArray();
    private const string Red = "C83232";
    private readonly E2EMedia _media;

    public ExportTransitionSourceEndToEndTests(E2EMedia media) => _media = media;

    /// <summary>A [0, 25) of <paramref name="aAsset"/> | B [25, 60) of a red clip (SourceIn 20) with a 10-frame
    /// <paramref name="type"/>: A needs its source frames 25..29; an existing file at the destination.</summary>
    private (ProjectBuilder P, Track V1, Clip A, Clip B, string Destination) Cut(string type, MediaAsset aAsset)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, aAsset, 0, 25);
        var b = p.Video(v1, _media.CopyOf(_media.Solid(Red, FrameRate.Fps25)), 25, 60, sourceInFrame: 20);
        v1.Transitions.Add(new Transition
        {
            TransitionTypeId = type, Duration = p.F(10), LeftClipId = a.Id, RightClipId = b.Id, Direction = TransitionRules.DefaultDirection(type)
        });
        Assert.Null(TransitionRules.ValidateTrack(v1, p.Rate));
        var destination = Path.Combine(_media.OutputFolder(), "export.mp4");
        File.WriteAllBytes(destination, Previous);
        return (p, v1, a, b, destination);
    }

    private static void AssertUntouched(string destination)
    {
        Assert.Equal(Previous, File.ReadAllBytes(destination));
        Assert.Equal(new[] { destination }, Directory.GetFiles(Path.GetDirectoryName(destination)!));
        AssertNoFfmpegLeft();
    }

    [FfmpegTheory]
    [InlineData(TransitionRules.CrossDissolve)]
    [InlineData(TransitionRules.Push)]
    public async Task A_file_replaced_by_a_shorter_one_is_refused_before_rendering_whatever_its_metadata_say(string type)
    {
        // The asset keeps the metadata of the 4 s pattern (100 frames), its file is the 1 s one (25 frames): the edit
        // rules and the preflight see enough media; only the decoder knows better.
        var longer = _media.Pattern();
        var shorter = _media.CopyOf(_media.Pattern(FrameRate.Fps25, seconds: 1));
        var replaced = new MediaAsset { FilePath = shorter.FilePath, Kind = MediaKind.Video, Metadata = longer.Metadata, AnalysisStatus = longer.AnalysisStatus };
        var (p, v1, a, b, destination) = Cut(type, replaced);
        var job = Preflight(p.Project, destination);

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            Service(EncoderHarness.Encoder()).ExportAsync(job, new SyncProgress(_ => { }), CancellationToken.None));

        Assert.Equal(ExportFailure.TransitionSourceMissing, error.Failure);
        Assert.Equal(new MissingTransitionSource(type, v1.Id, a.Id, b.Id, p.F(25), BeforeRendering: true), error.MissingSource);
        AssertUntouched(destination);
    }

    [FfmpegFact]
    public async Task A_decoder_that_runs_out_while_rendering_deletes_the_unfinished_output_and_keeps_the_destination()
    {
        // The check before rendering decodes the full file; the export's own reader then gets the 1 s file: the decoder
        // ends at A's end, after the encoder has started writing.
        var full = _media.CopyOf(_media.Pattern());
        var shorter = _media.CopyOf(_media.Pattern(FrameRate.Fps25, seconds: 1));
        var (p, v1, a, b, destination) = Cut(TransitionRules.CrossDissolve, full);
        var job = Preflight(p.Project, destination);
        var opens = 0;
        var decoder = new RedirectingDecoder(new FfmpegVideoDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegVideoDecoder>.Instance, luts: Luts),
            r => r.FilePath == full.FilePath && Interlocked.Increment(ref opens) > 1 ? r with { FilePath = shorter.FilePath } : r);
        var service = new ExportService(FfmpegTools.FfmpegLocator, decoder,
            new FfmpegAudioDecoder(FfmpegTools.FfmpegLocator, NullLogger<FfmpegAudioDecoder>.Instance),
            EncoderHarness.Encoder(), () => new AvaloniaCompositionRasterizer(), NullLogger<ExportService>.Instance);
        var video = new List<ExportProgress>();

        var error = await Assert.ThrowsAsync<ExportException>(() =>
            service.ExportAsync(job, new SyncProgress(r => { if (r.Stage == ExportStage.Video) video.Add(r); }), CancellationToken.None));

        Assert.Equal(ExportFailure.TransitionSourceMissing, error.Failure);
        Assert.Equal(new MissingTransitionSource(TransitionRules.CrossDissolve, v1.Id, a.Id, b.Id, p.F(25), BeforeRendering: false), error.MissingSource);
        Assert.Contains(video, r => r.Done >= 20);                                           // it was rendering
        AssertUntouched(destination);                                                         // no partial file left
    }

    [FfmpegFact]
    public async Task A_phase_27_project_with_dissolves_exports_exactly_as_before()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a = p.Video(v1, _media.Pattern(), 0, 40);
        var b = p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 40, 80, sourceInFrame: 20);
        var c = p.Video(v1, _media.Pattern(), 80, 100, sourceInFrame: 2);
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = p.F(20), LeftClipId = a.Id, RightClipId = b.Id });
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = p.F(2), LeftClipId = b.Id, RightClipId = c.Id });   // a legacy 2-frame one

        // The same project as a v7 file (the Phase 27 format: dissolves only), read by this build.
        var folder = _media.OutputFolder();
        var json = ProjectSerializer.Serialize(p.Project, folder);
        Assert.Contains("\"formatVersion\": 9", json);
        var v7 = ProjectSerializer.Deserialize(json.Replace("\"formatVersion\": 9", "\"formatVersion\": 7"), folder);

        var built = await ExportProject(p.Project, Path.Combine(folder, "built.mp4"));
        var loaded = await ExportProject(v7, Path.Combine(_media.OutputFolder(), "v7.mp4"));

        AssertValidMp4(loaded);
        Assert.Equal(built.Canvases.Count, loaded.Canvases.Count);
        for (var n = 0; n < built.Canvases.Count; n++)
            Assert.True(built.Canvases[n].AsSpan().SequenceEqual(loaded.Canvases[n]), $"frame {n} differs");
    }

    /// <summary>A decoder that changes the request first (a file that is not what the first decode found).</summary>
    private sealed class RedirectingDecoder(IVideoDecoder inner, Func<VideoDecodeRequest, VideoDecodeRequest> redirect) : IVideoDecoder
    {
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) => inner.OpenAsync(redirect(request), ct);
    }
}
