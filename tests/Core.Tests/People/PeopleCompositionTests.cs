using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.People;
using AiVideoEditor.Core.Playback;
using Xunit;
using static AiVideoEditor.Core.Tests.People.PeopleTestData;

namespace AiVideoEditor.Core.Tests.People;

/// <summary>
/// D061 in the shared snapshot and draw plan (the Preview and the export draw from them alike): Follow moves the frame, its
/// outline and its blurred background together on the shown frame; Occlusion gives the outline the people's mask of that
/// frame; without the analyses — or with the features off — every plan is exactly as before; the analyses are presentation
/// only; the export's preflight waits for a running analysis and warns when Follow or Occlusion cannot work.
/// </summary>
public class PeopleCompositionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate, FrameWidth = 1080, FrameHeight = 1920 } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly MediaAsset _asset;

    public PeopleCompositionTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _asset = new MediaAsset
        {
            FilePath = @"C:\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = S(20), FrameRate = Rate, StartTime = S(0),
                Width = 3840, Height = 2160, DisplayRotation = 90, DisplayWidth = 2160, DisplayHeight = 3840
            }
        };
        _project.MediaAssets.Add(_asset);
    }

    private VideoClip Clip(FollowSettings? follow = null, bool occlusion = false, double scale = 2, bool routes = false)
    {
        var clip = new VideoClip
        {
            MediaAssetId = _asset.Id, TimelineStart = F(0), Duration = S(10), Scale = scale, Follow = follow, HidesOutlinesBehindPeople = occlusion
        };
        clip.SourceOut = clip.Duration;
        if (routes)
        {
            var square = ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.6, 0.6), new NormalizedPoint(0.4, 0.6));
            clip.Routes = ImmutableArray.Create(RouteColor.WithResolved(new HoldMarkup
            {
                Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), square, RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), S(0)))
            }));
        }
        _v1.Clips.Add(clip);
        return clip;
    }

    /// <summary>One climber walking from x 0.3 to 0.7 over 10 s.</summary>
    private static PersonTracks Walk() => Tracks(Enumerable.Range(0, 101).Select(i => new[] { Person(0.3 + i * 0.004, 0.5) }).ToArray());

    private static PersonMaskTrack MaskOverOutline()
    {
        var grid = new byte[36 * 64];
        for (var y = 25; y < 40; y++) for (var x = 14; x < 22; x++) grid[y * 36 + x] = 255;   // around (0.5, 0.5)
        var samples = Enumerable.Range(0, 151).Select(i => new PersonMaskSample(S(i / 15.0),
            ImmutableArray.Create(MaskCodec.Entry(1, grid, 36, 64)!))).ToImmutableArray();
        return new PersonMaskTrack(36, 64, PeopleAnalyzer.MaskInterval, samples, "test");
    }

    private CompositionDrawPlan PlanOn(double frameSeconds, PeopleAnalyses? people)
    {
        var snapshot = PlaybackSnapshotBuilder.Build(_project, 1, people: people);
        var frame = new DecodedFrame(1080, 1920, 1080 * 4, new byte[1080 * 1920 * 4],
            new SourceTimestamp((long)Math.Round(frameSeconds * 25), new TimeBase(1, 25)));
        var layers = snapshot.LayersAt(F((long)Math.Round(frameSeconds * 25))).Select(l => new ResolvedLayer(l, frame));
        return CompositionDrawPlan.Build(new FrameSize(1080, 1920), Affine2D.Identity, layers);
    }

    private PeopleAnalyses With(PersonTracks? tracks = null, PersonMaskTrack? masks = null) => new(
        tracks is null ? new Dictionary<Guid, PersonTracks>() : new Dictionary<Guid, PersonTracks> { [_asset.Id] = tracks },
        masks is null ? new Dictionary<Guid, PersonMaskTrack>() : new Dictionary<Guid, PersonMaskTrack> { [_asset.Id] = masks });

    [Fact]
    public void Follow_moves_the_frame_and_its_outline_together_on_the_shown_frame()
    {
        Clip(new FollowSettings(), routes: true);
        var people = With(Walk());
        var early = PlanOn(1, people);
        var late = PlanOn(9, people);
        var frameEarly = early.Operations.OfType<FrameDraw>().Single();
        var frameLate = late.Operations.OfType<FrameDraw>().Single();
        Assert.True(frameLate.Transform.Tx < frameEarly.Transform.Tx - 100, "the picture moves left as the climber walks right");
        var outlineEarly = early.Operations.OfType<OutlineDraw>().Single();
        Assert.Equal(frameEarly.Transform, outlineEarly.Transform);   // the outline moves with its frame
        Assert.Equal(frameLate.Transform, late.Operations.OfType<OutlineDraw>().Single().Transform);
    }

    [Fact]
    public void Without_the_analyses_or_with_Follow_off_the_plan_is_exactly_as_before()
    {
        var clip = Clip(routes: true);
        var before = PlanOn(5, null).Operations;
        Assert.Equal(before, PlanOn(5, With(Walk(), MaskOverOutline())).Operations, new OperationComparer());
        clip.Follow = new FollowSettings();
        Assert.Equal(before, PlanOn(5, null).Operations, new OperationComparer());   // Follow on, nothing analysed yet
    }

    [Fact]
    public void Several_people_without_a_pick_keep_the_clip_framing()
    {
        Clip(new FollowSettings(), routes: true);
        var two = Tracks(Enumerable.Range(0, 101).Select(i => new[] { Person(0.3, 0.5, colour: 0), Person(0.7, 0.5, colour: 12) }).ToArray());
        Assert.Equal(PlanOn(5, null).Operations, PlanOn(5, With(two)).Operations, new OperationComparer());
    }

    [Fact]
    public void A_pick_chooses_the_followed_person()
    {
        var two = Tracks(Enumerable.Range(0, 101).Select(i => new[] { Person(0.3, 0.5, colour: 0), Person(0.7, 0.5, colour: 12) }).ToArray());
        var clip = Clip(new FollowSettings(S(1), new NormalizedPoint(0.7, 0.5)));
        var right = PlanOn(5, With(two)).Operations.OfType<FrameDraw>().Single().Transform.Tx;
        clip.Follow = new FollowSettings(S(1), new NormalizedPoint(0.3, 0.5));
        var left = PlanOn(5, With(two)).Operations.OfType<FrameDraw>().Single().Transform.Tx;
        Assert.True(left > right, "following the left person shifts the picture to the right");
    }

    [Fact]
    public void Occlusion_gives_the_outline_the_mask_of_the_shown_frame()
    {
        Clip(occlusion: true, routes: true);
        var outline = PlanOn(5, With(Walk(), MaskOverOutline())).Operations.OfType<OutlineDraw>().Single();
        Assert.NotNull(outline.Occlusion);
        Assert.Equal(new RectD(0, 0, 2160, 3840), outline.OcclusionRect);
        Assert.Equal(255, outline.Occlusion!.At(0.5, 0.5));
        // Only the clips that ask for it.
        _v1.Clips.Clear();
        Clip(routes: true);
        Assert.Null(PlanOn(5, With(Walk(), MaskOverOutline())).Operations.OfType<OutlineDraw>().Single().Occlusion);
    }

    [Fact]
    public void The_people_analyses_are_presentation_only()
    {
        Clip(new FollowSettings(), occlusion: true, routes: true);
        var without = PlaybackSnapshotBuilder.Build(_project, 1);
        var with = PlaybackSnapshotBuilder.Build(_project, 2, people: With(Walk(), MaskOverOutline()));
        Assert.True(without.DiffersOnlyInPresentation(with));
        Assert.NotNull(with.VideoLayers[0].Spans[0].Follow);
        Assert.NotNull(with.VideoLayers[0].Spans[0].PeopleMasks);
    }

    [Fact]
    public void The_export_waits_for_a_running_analysis()
    {
        Clip(new FollowSettings());
        var result = Preflight(new ExportPeople(PeopleAnalyses.None, new HashSet<Guid> { _asset.Id }));
        Assert.Contains(result.Issues, i => i.Kind == ExportIssueKind.PeopleAnalysisPending && i.Severity == ExportIssueSeverity.Error);
        Assert.False(result.CanExport);
    }

    [Fact]
    public void The_export_warns_when_Follow_cannot_move_or_outlines_cannot_be_hidden()
    {
        Clip(new FollowSettings(), occlusion: true, scale: 1, routes: true);
        var result = Preflight(new ExportPeople(With(Walk()), new HashSet<Guid>()) { FailedMasks = new Dictionary<Guid, string> { [_asset.Id] = "no GPU" } });
        Assert.True(result.CanExport, string.Join(" | ", result.Issues.Select(i => i.Kind + ": " + i.Message)));
        Assert.Contains(result.Issues, i => i.Kind == ExportIssueKind.FollowUnavailable && i.Message.Contains("«"));
        Assert.Contains(result.Issues, i => i.Kind == ExportIssueKind.OcclusionUnavailable && i.Message.Contains("no GPU"));
    }

    [Fact]
    public void A_working_Follow_and_Occlusion_export_without_a_people_issue()
    {
        Clip(new FollowSettings(), occlusion: true, routes: true);
        var result = Preflight(new ExportPeople(With(Walk(), MaskOverOutline()), new HashSet<Guid>()));
        Assert.DoesNotContain(result.Issues, i => i.Kind is ExportIssueKind.PeopleAnalysisPending or ExportIssueKind.FollowUnavailable or ExportIssueKind.OcclusionUnavailable);
        Assert.NotNull(result.Job!.Snapshot.VideoLayers[0].Spans[0].Follow);
    }

    private ExportPreflightResult Preflight(ExportPeople people) => ExportPreflight.Check(_project, @"C:\out\video.mp4",
        new ExportPreflightEnvironment(true, _ => true, _ => true, path => path.TrimEnd('\\') == @"C:\out") { People = people });

    /// <summary>Compares draw operations by value (records holding arrays compare their contents).</summary>
    private sealed class OperationComparer : IEqualityComparer<DrawOperation>
    {
        public bool Equals(DrawOperation? a, DrawOperation? b) => (a, b) switch
        {
            (FrameDraw x, FrameDraw y) => x.Transform == y.Transform && x.LocalRect == y.LocalRect && x.FrameSourceRect == y.FrameSourceRect && x.Opacity == y.Opacity,
            (OutlineDraw x, OutlineDraw y) => x.Transform == y.Transform && x.Occlusion is null && y.Occlusion is null &&
                                              x.Polygons.Length == y.Polygons.Length && x.Polygons.Zip(y.Polygons).All(p => p.First.SequenceEqual(p.Second)),
            _ => Equals(a, b)
        };

        public int GetHashCode(DrawOperation obj) => 0;
    }
}
