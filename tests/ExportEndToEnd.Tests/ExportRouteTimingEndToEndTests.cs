using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// D048 §5, stage 2 criterion 9 and stage 3 criterion 4 (Phases 33 / 34): the <see cref="HoldAnimationMode.Duration"/>
/// animation through the whole export with real parts (ffmpeg decoding → the shared draw plan → the Avalonia rasterizer →
/// H.264). For the three orders of §3.3 and after the real edit service's layout (§3.4), speed change (§3.8), head trim
/// (§3.8) and a strip edit (§4.2), the Preview (its own pipeline and control, the same snapshot) draws exactly the export's
/// canvas bytes on every checked frame, the MP4 matches the canvases within the codec's error, and the outline's pixels
/// follow the 80 / 20 schedule of §3.2: hidden before its hold's start, fully shown from <c>n0 + P</c> to <c>T − 1</c>, gone
/// at <c>T</c> with no fade.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportRouteTimingEndToEndTests(E2EMedia media, ITestOutputHelper output)
{
    private const string Red = "C83232";

    private static ImmutableArray<NormalizedPoint> Box(double x0, double y0, double x1, double y1) => ImmutableArray.Create(
        new NormalizedPoint(x0, y0), new NormalizedPoint(x1, y0), new NormalizedPoint(x1, y1), new NormalizedPoint(x0, y1));

    // Three boxes (320 × 180 canvas): H0 low left, H1 mid right, H2 top middle — a different rank in every order.
    private static readonly (double X0, double Y0, double X1, double Y1)[] Boxes =
        [(0.05, 0.6, 0.25, 0.9), (0.75, 0.35, 0.95, 0.65), (0.40, 0.05, 0.60, 0.35)];

    /// <summary>The white core of a box's left edge at mid-height on the red wall: 0 = hidden … 1 = fully shown.</summary>
    private static double White(byte[] canvas, int width, int height, int box)
    {
        var (x0, y0, _, y1) = Boxes[box];
        return (Pixel(canvas, width, (int)Math.Round(x0 * width), (int)Math.Round((y0 + y1) / 2 * height)).G - 0x32) / (255.0 - 0x32);
    }

    private static HoldMarkup Markup(HoldAnimation? animation) => new()
    {
        StrokeWidth1080 = 32, ColorHex = "#FFFFFF", Keyline = KeylineMode.On, KeylineActive = true,
        Holds = Boxes.Select(b => new Hold(Guid.NewGuid(), Box(b.X0, b.Y0, b.X1, b.Y1), default, default, MediaTime.Zero)).ToImmutableArray(),
        Animation = animation
    };

    private async Task AssertPreviewMatchesExport(ExportRun run, IEnumerable<long> frames)
    {
        foreach (var n in frames.Where(n => n >= 0 && n < run.Canvases.Count).Distinct())
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var differing = Enumerable.Range(0, preview.Pixels.Length).Count(i => preview.Pixels[i] != run.Canvases[(int)n][i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
    }

    private void AssertMp4MatchesCanvases(ExportRun run)
    {
        var decoded = EncoderHarness.DecodeFrames(run.Path, run.Output.Size.Width, run.Output.Size.Height);
        var errors = decoded.Select((frame, n) => MeanAbs(frame, run.Canvases[n])).ToArray();
        output.WriteLine($"{run.Output.FrameCount} frames, MP4 vs canvas mean |Δ| max {errors.Max():0.00}, avg {errors.Average():0.00}");
        Assert.All(errors, e => Assert.InRange(e, 0, 3.0));
        AssertValidMp4(run);
    }

    [FfmpegTheory]
    [InlineData(HoldAnimationOrder.BottomToTop, new[] { 0, 1, 2 })]
    [InlineData(HoldAnimationOrder.LeftToRight, new[] { 0, 2, 1 })]
    [InlineData(HoldAnimationOrder.RightToLeft, new[] { 1, 2, 0 })]
    public async Task Each_order_follows_the_80_20_schedule_and_the_preview_matches_the_export(HoldAnimationOrder order, int[] sequence)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);   // 320 × 180
        var clip = p.Video(p.VideoTrack(), media.Solid(Red, FrameRate.Fps25), 0, 100);
        // From source 0.4 s (n0 = 10) for 2 s (F = 50, T = 60): P = ⌊(400 + 5) / 10⌋ = 40, ranks start at 10, 23, 36;
        // a 0.2 s appear fade (R = 5 frames); all shown on [50, 60), none at 60.
        clip.HoldMarkup = Markup(new HoldAnimation
        {
            Mode = HoldAnimationMode.Duration, Order = order, StartSourceTime = MediaTime.FromSeconds(0.4),
            Duration = MediaTime.FromSeconds(2), AppearFade = MediaTime.FromSeconds(0.2)
        });
        long[] starts = [10, 23, 36];

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), $"duration-{order}.mp4"));
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);

        await AssertPreviewMatchesExport(run, [9, 10, 12, 22, 23, 28, 35, 36, 41, 49, 50, 59, 60, 61]);

        for (var rank = 0; rank < 3; rank++)
        {
            var (box, start) = (sequence[rank], starts[rank]);
            Assert.InRange(White(run.Canvases[(int)start - 1], w, h, box), -0.01, 0.01);           // not before its start
            Assert.InRange(White(run.Canvases[(int)start + 2], w, h, box), 3 / 6.0 - 0.02, 3 / 6.0 + 0.02);  // fading in
            Assert.InRange(White(run.Canvases[(int)start + 5], w, h, box), 0.99, 1.0);
        }
        for (var box = 0; box < 3; box++)
        {
            Assert.InRange(White(run.Canvases[50], w, h, box), 0.99, 1.0);    // n0 + P: the whole route
            Assert.InRange(White(run.Canvases[59], w, h, box), 0.99, 1.0);    // T − 1
            Assert.InRange(White(run.Canvases[60], w, h, box), -0.01, 0.01);  // T: gone, no fade
        }
        AssertMp4MatchesCanvases(run);
    }

    [FfmpegFact]
    public async Task Two_routes_on_one_clip_draw_each_by_its_own_schedule_and_the_preview_matches_the_export()
    {
        // D053: route A (box 0) animated on [10, 35) and gone at 35; route B (box 2) on [50, 75) — one clip, two outlines.
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), media.Solid(Red, FrameRate.Fps25), 0, 100);
        HoldMarkup Route(int box, double start) => Markup(new HoldAnimation
        {
            Mode = HoldAnimationMode.Duration, StartSourceTime = MediaTime.FromSeconds(start), Duration = MediaTime.FromSeconds(1),
            AppearFade = MediaTime.FromSeconds(0.04)
        }) with { Holds = Markup(null).Holds.Where((_, i) => i == box).ToImmutableArray() };
        clip.Routes = ImmutableArray.Create(Route(0, 0.4), Route(2, 2.0));

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), "two-routes.mp4"));
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        await AssertPreviewMatchesExport(run, [9, 10, 20, 34, 35, 49, 50, 60, 74, 75]);

        Assert.InRange(White(run.Canvases[9], w, h, 0), -0.01, 0.01);     // A not yet
        Assert.InRange(White(run.Canvases[30], w, h, 0), 0.99, 1.0);      // A shown
        Assert.InRange(White(run.Canvases[30], w, h, 2), -0.01, 0.01);    // B not yet
        Assert.InRange(White(run.Canvases[35], w, h, 0), -0.01, 0.01);    // A gone at its T
        Assert.InRange(White(run.Canvases[70], w, h, 2), 0.99, 1.0);      // B shown
        Assert.InRange(White(run.Canvases[70], w, h, 0), -0.01, 0.01);    // A stays gone
        Assert.InRange(White(run.Canvases[75], w, h, 2), -0.01, 0.01);    // B gone at its T
        AssertMp4MatchesCanvases(run);
    }

    [FfmpegFact]
    public async Task After_the_layout_a_speed_change_a_head_trim_and_a_strip_edit_the_preview_matches_the_export()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var clip = p.Video(p.VideoTrack(), media.Solid(Red, FrameRate.Fps25), 0, 100);
        clip.HoldMarkup = Markup(null);
        var edit = new TimelineEditService(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        Assert.True(edit.LayOutIntroAndFinal(clip.Id, p.F(75)).Success);                       // §3.4 at T = 3 s (short intro)
        var template = p.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        Assert.True(edit.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top } }).Success);
        Assert.True(edit.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(10)).Success);               // 0.5×: 200 frames
        Assert.True(edit.TrimClip(clip.Id, ClipEdge.Start, p.F(10)).Success);                   // head trim into the green
        Assert.True(edit.EditOverlayStrip(clip.Id, OverlayStripHandle.OrangeStart, -6).Success); // a longer animation

        var strip = edit.OverlayStripOf(clip.Id)!;
        var orange = strip.Orange!.Value;
        var a = clip.HoldMarkup!.Animation!;
        Assert.Equal(HoldAnimationMode.Duration, a.Mode);
        // The strip shows exactly the frames the composition draws (the shared rule).
        var n0 = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, p.Rate);
        Assert.Equal(new FrameSpan(n0, n0 + HoldAnimationRule.DurationFrames(a, p.Rate)), orange);
        var schedule = HoldAnimationRule.Schedule(a, clip.HoldMarkup.Holds, n0, p.Rate);
        output.WriteLine($"strip {strip}; first appear {schedule.AppearFrames.Min()}, last {schedule.AppearFrames.Max()}");

        var run = await ExportProject(p.Project, Path.Combine(media.OutputFolder(), "duration-edited.mp4"));
        var (w, h) = (run.Output.Size.Width, run.Output.Size.Height);
        var p80 = orange.Start + HoldAnimationRule.AppearFrames(orange.Length);

        await AssertPreviewMatchesExport(run, [strip.Clip.Start, strip.Green!.Value.Start, orange.Start - 1, orange.Start,
            orange.Start + 3, p80 - 1, p80, orange.End - 1, orange.End, orange.End + 1, strip.Lilac!.Value.Start,
            strip.Lilac.Value.Start + 10, strip.Clip.End - 1]);

        var first = Array.IndexOf(schedule.AppearFrames.ToArray(), schedule.AppearFrames.Min());
        Assert.InRange(White(run.Canvases[(int)orange.Start - 1], w, h, first), -0.01, 0.01);
        for (var box = 0; box < 3; box++)
        {
            Assert.InRange(White(run.Canvases[(int)p80], w, h, box), 0.99, 1.0);
            Assert.InRange(White(run.Canvases[(int)orange.End - 1], w, h, box), 0.99, 1.0);
            Assert.InRange(White(run.Canvases[(int)orange.End], w, h, box), -0.01, 0.01);
        }
        AssertMp4MatchesCanvases(run);
    }

    /// <summary>What the edit service needs from the project service: the scene's project and the notifications.</summary>
    private sealed class SceneProject(Core.Entities.Project project) : IProjectService
    {
        public Core.Entities.Project Current { get; } = project;
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public event EventHandler? TimelineChanged { add { } remove { } }
        public event EventHandler? SaveStateChanged { add { } remove { } }
        public event EventHandler? ProjectSaved { add { } remove { } }
        public event EventHandler<MediaAvailabilityChangedEventArgs>? MediaAvailabilityChanged { add { } remove { } }
        public event EventHandler<MediaRelinkedEventArgs>? MediaRelinked { add { } remove { } }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => throw new NotSupportedException();
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public void SetTraining(Core.Telemetry.TrainingReference? training) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecheckMediaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyMediaRelinked(IReadOnlyList<MediaFileReplacement> replacements) { }
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => throw new NotSupportedException();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }
}
