using System.Collections.Immutable;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D051 §5.2–§5.7 through the edit service (T4, T7), at 25 fps on a 20 s video whose Z is the training's W0 (CC = 0):
/// climb A at W0 + 15 s (proposed at 3 s), B at W0 + 5 s (clamped to frame 0), C at W0 + 40 s (not filmed). The proposals
/// of a Z in its Undo step; move; link / unlink and their refusals both ways; «Разметить трассу от метки» at speeds 1, 2
/// and 0.5 keeping positionChangedAt; decline / Undo / «Вернуть»; the link following its markup on delete and split; the
/// paste refusal of a duplicate FIT markup; positionChangedAt set exactly where the effective start changes.
/// </summary>
public sealed class FitMarkEditTests
{
    private const long S = TimeSpan.TicksPerSecond;
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly TimelineFixture _f = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly TimelineEditService _svc;
    private readonly MediaAsset _video;
    private readonly TrainingReference _training;
    private readonly ClimbIdentity _a, _b;

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public DateTimeOffset Tick() => Now = Now.AddMinutes(1);
    }

    private static WorkoutSegment Climb(int index, double startSeconds) =>
        new(index, WorkoutSegmentKind.Climb, index / 2 + 1, W0.AddSeconds(startSeconds), W0.AddSeconds(startSeconds + 20), MediaTime.FromSeconds(20), null, null);

    public FitMarkEditTests()
    {
        _svc = new TimelineEditService(_f.Projects, _f.UndoRedo, NullLogger<TimelineEditService>.Instance, null, _time);
        _video = _f.Video(20, Rate);
        var climbs = new[] { Climb(0, 15), Climb(2, 5), Climb(4, 40) };
        _training = new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1), UtcOffset = null,
                ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900), Sport = 31, SubSport = 69,
                AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = climbs, Warnings = []
            },
            Availability = TrainingAvailability.Available
        };
        _f.Projects.SetTraining(_training);
        _a = FitMarkRules.IdentityOf(climbs[0]);
        _b = FitMarkRules.IdentityOf(climbs[1]);
    }

    private FitMark Mark(ClimbIdentity climb) => _training.Marks.Single(m => m.Climb == climb);

    private void Propose() => Assert.True(_svc.SetRecordingZeroOverride(_video.Id, W0).Success);

    private static ImmutableArray<NormalizedPoint> Square(double y) => ImmutableArray.Create(
        new NormalizedPoint(0.48, y - 0.02), new NormalizedPoint(0.52, y - 0.02), new NormalizedPoint(0.52, y + 0.02), new NormalizedPoint(0.48, y + 0.02));

    /// <summary>A clip of the video with holds, at <paramref name="startFrame"/>; laid out with T at <paramref name="tFrame"/>
    /// (a Duration animation) unless null.</summary>
    private VideoClip MarkedClip(long startFrame = 0, long? tFrame = 200)
    {
        Assert.True(_svc.AddClip(_video.Id, _f.V1.Id, MediaTime.FromFrame(startFrame, Rate)).Success);
        var clip = _f.V1.Clips.OfType<VideoClip>().Single(c => c.TimelineStart == MediaTime.FromFrame(startFrame, Rate));
        foreach (var y in new[] { 0.3, 0.6 })
            Assert.True(_svc.AddHold(clip.Id, new Hold(Guid.NewGuid(), Square(y), RouteColor.HexToOkLab("#E53935"),
                RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero)).Success);
        if (tFrame is { } t) Assert.True(_svc.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(startFrame + t, Rate)).Success);
        return clip;
    }

    private static long OrangeEnd(VideoClip clip)
    {
        var a = clip.HoldMarkup!.Animation!;
        return HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, a.StartSourceTime, Rate) + HoldAnimationRule.DurationFrames(a, Rate);
    }

    // ---- Proposals of a Z (B3) ------------------------------------------------------------------------------------------

    [Fact]
    public void Setting_Z_proposes_the_marks_in_the_same_undo_step_and_Redo_brings_the_same_marks()
    {
        var created = _time.Now;
        Propose();
        Assert.Equal(2, _training.Marks.Count);
        Assert.Equal(new FitMark(_a, W0.AddSeconds(3), 120_000_000, 0, _video.Id, FitMarkPosition.Proposed, created), Mark(_a));
        Assert.Equal(new FitMark(_b, W0.AddSeconds(-7), 120_000_000, 0, _video.Id, FitMarkPosition.Clamped(MediaTime.Zero), created), Mark(_b));
        var marks = _training.Marks.ToList();

        _f.UndoRedo.Undo();
        Assert.Null(_video.RecordingZeroOverrideUtc);
        Assert.Empty(_training.Marks);
        _time.Tick();
        _f.UndoRedo.Redo();
        Assert.Equal(W0, _video.RecordingZeroOverrideUtc);
        Assert.Equal(marks, _training.Marks);
    }

    [Fact]
    public void A_new_Z_keeps_the_kinds_proposes_nothing_new_and_sets_no_position_time()
    {
        Propose();
        _time.Tick();
        Assert.True(_svc.MoveFitMark(_b, MediaTime.FromSeconds(2)).Success);
        var before = _training.Marks.ToList();
        _time.Tick();
        Assert.True(_svc.SetRecordingZeroOverride(_video.Id, W0.AddSeconds(1)).Success);
        Assert.Equal(before, _training.Marks);
        Assert.Equal(new MediaTime(2 * S), FitMarkRules.EffectiveStart(Mark(_a), _f.Project)!.Value.SourceTime);   // follows W_auto
    }

    [Fact]
    public void The_clock_correction_creates_and_moves_no_mark()
    {
        Propose();
        var before = _training.Marks.ToList();
        Assert.True(_svc.SetTrainingClockCorrection(new MediaTime(2 * S)).Success);
        Assert.Equal(before, _training.Marks);
    }

    [Fact]
    public void Without_a_usable_FIT_a_Z_proposes_nothing()
    {
        _training.Availability = TrainingAvailability.Missing;
        Propose();
        Assert.Empty(_training.Marks);
    }

    // ---- Move ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_mark_makes_it_moved_and_sets_its_position_time_once_per_frame()
    {
        Propose();
        var created = Mark(_a).PositionChangedAt;
        var moved = _time.Tick();
        Assert.True(_svc.MoveFitMark(_a, MediaTime.FromSeconds(4)).Success);
        Assert.Equal((FitMarkPosition.Moved(MediaTime.FromSeconds(4)), moved), (Mark(_a).Position, Mark(_a).PositionChangedAt));
        _time.Tick();
        Assert.True(_svc.MoveFitMark(_a, MediaTime.FromSeconds(4) + new MediaTime(1)).NoChange);   // the same frame
        Assert.False(_svc.MoveFitMark(_a, MediaTime.FromSeconds(20)).Success);
        Assert.False(_svc.MoveFitMark(_a, new MediaTime(-1)).Success);

        _f.UndoRedo.Undo();
        Assert.Equal((FitMarkPosition.Proposed, created), (Mark(_a).Position, Mark(_a).PositionChangedAt));
    }

    [Fact]
    public void Moving_a_clamped_mark_makes_it_usable()
    {
        Propose();
        Assert.False(FitMarkRules.IsUsable(Mark(_b)));
        Assert.True(_svc.MoveFitMark(_b, MediaTime.FromSeconds(1)).Success);
        Assert.True(FitMarkRules.IsUsable(Mark(_b)));
        _f.UndoRedo.Undo();
        Assert.False(FitMarkRules.IsUsable(Mark(_b)));
    }

    // ---- Link / unlink ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_link_is_unique_both_ways_and_only_to_a_Duration_animation()
    {
        Propose();
        var first = MarkedClip(0);
        var second = MarkedClip(600);
        var interval = MarkedClip(1200, null);
        Assert.True(_svc.SetHoldAnimation(interval.Id, new HoldAnimation()).Success);

        var linked = _time.Tick();
        Assert.True(_svc.LinkFitClimb(_a, first.Id).Success);
        Assert.Equal((first.Id, (DateTimeOffset?)linked), (Mark(_a).LinkedClipId!.Value, (DateTimeOffset?)Mark(_a).PositionChangedAt));   // 3 s → T 8 s

        Assert.Equal(Messages.FitMark_ClimbLinked, _svc.LinkFitClimb(_a, second.Id).Message);
        Assert.Equal(Messages.FitMark_AnimationLinked, _svc.LinkFitClimb(_b, first.Id).Message);
        Assert.Equal(Messages.FitMark_NotDurationAnimation, _svc.LinkFitClimb(_b, interval.Id).Message);
        Assert.True(_svc.LinkFitClimb(_a, first.Id).NoChange);
        Assert.Equal(first.Id, Mark(_a).LinkedClipId);

        var unlinked = _time.Tick();
        Assert.True(_svc.UnlinkFitClimb(_a).Success);
        Assert.Equal((null, unlinked), (Mark(_a).LinkedClipId, Mark(_a).PositionChangedAt));
        Assert.Equal(Messages.FitMark_NotLinked, _svc.UnlinkFitClimb(_a).Message);
        Assert.True(_svc.LinkFitClimb(_b, first.Id).Success);                 // free again both ways
        _f.UndoRedo.Undo();
        _f.UndoRedo.Undo();
        Assert.Equal((first.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(10)]
    public void Laying_the_route_out_from_a_mark_links_it_with_T_on_its_frame_and_keeps_the_position_time(int speedSteps)
    {
        Propose();
        var clip = MarkedClip(0, null);
        if (speedSteps != 20) Assert.True(_svc.SetClipSpeed(clip.Id, ClipSpeed.FromSteps(speedSteps)).Success);
        var created = Mark(_a).PositionChangedAt;
        _time.Tick();

        Assert.True(_svc.LayOutRouteFromMark(_a, clip.Id).Success);
        Assert.Equal((clip.Id, created), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
        var markFrame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, MediaTime.FromSeconds(3), Rate);
        Assert.Equal(markFrame, OrangeEnd(clip));

        _f.UndoRedo.Undo();
        Assert.Null(Mark(_a).LinkedClipId);
        Assert.Null(clip.HoldMarkup!.Animation);
    }

    [Fact]
    public void Laying_out_from_a_mark_on_the_first_frame_makes_no_animation_and_no_link()
    {
        Propose();
        var clip = MarkedClip(0, null);
        var created = Mark(_b).PositionChangedAt;
        var result = _svc.LayOutRouteFromMark(_b, clip.Id);                // T on frame 0: no time before it (D048 §3.4)
        Assert.True(result.Success);
        Assert.Equal(Messages.Layout_NoAnimationTime, result.Message);
        Assert.Equal((null, created), (Mark(_b).LinkedClipId, Mark(_b).PositionChangedAt));
        Assert.False(FitMarkRules.IsUsable(Mark(_b)));
    }

    private sealed class FixedEstimate(long ticks) : IStartCorrectionSource
    {
        public FitCalibrationSnapshot Current { get; } = FitCalibrationSnapshot.Initial with { EstimateTicks = ticks, EstimateVersion = 3 };
    }

    [Fact]
    public void A_mark_clamped_to_the_last_frame_laid_out_from_becomes_a_full_observation_with_its_position_time_kept()
    {
        // D051 §5.8 / Q2: a negative estimate (−20 s) puts W_auto after the video; the mark is clamped to its last frame.
        var svc = new TimelineEditService(_f.Projects, _f.UndoRedo, NullLogger<TimelineEditService>.Instance, new FixedEstimate(-200_000_000), _time);
        Assert.True(svc.SetRecordingZeroOverride(_video.Id, W0).Success);
        var mark = Mark(_b);                                               // B at W0 + 5 s: W_auto = W0 + 25 s, after the 20 s video
        Assert.Equal((FitMarkPosition.Clamped(MediaTime.FromFrame(499, Rate)), -200_000_000L, 3), (mark.Position, mark.StartCorrectionTicks, mark.CalibrationVersion));
        Assert.DoesNotContain(FitMarkRules.Observations(_f.Project), o => o.Key.Climb == _b);

        var clip = MarkedClip(0, null);
        _time.Tick();
        Assert.True(svc.LayOutRouteFromMark(_b, clip.Id).Success);
        Assert.Equal((clip.Id, mark.PositionChangedAt), (Mark(_b).LinkedClipId!.Value, Mark(_b).PositionChangedAt));
        // c = W_FITStart − (Z + s_T + CC) = (W0 + 5 s) − (W0 + 499 frames).
        var observation = Assert.Single(FitMarkRules.Observations(_f.Project), o => o.Key.Climb == _b);
        Assert.Equal(5 * S - MediaTime.FromFrame(499, Rate).Ticks, observation.Value);
        Assert.Equal(mark.PositionChangedAt, observation.PositionChangedAt);

        _f.UndoRedo.Undo();
        Assert.Null(Mark(_b).LinkedClipId);
        Assert.DoesNotContain(FitMarkRules.Observations(_f.Project), o => o.Key.Climb == _b);
    }

    [Fact]
    public void Another_climbs_animation_or_another_video_refuses_the_layout()
    {
        Propose();
        var clip = MarkedClip(0);
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        Assert.Equal(Messages.FitMark_AnimationLinked, _svc.LayOutRouteFromMark(_b, clip.Id).Message);
        var other = _f.Video(20, Rate, "other.mp4");
        Assert.True(_svc.AddClip(other.Id, _f.V1.Id, MediaTime.FromFrame(2000, Rate)).Success);
        var otherClip = _f.V1.Clips.OfType<VideoClip>().Single(c => c.MediaAssetId == other.Id);
        Assert.Equal(Messages.FitMark_ClipNotOnMarkVideo, _svc.LayOutRouteFromMark(_b, otherClip.Id).Message);
    }

    // ---- Decline / «Вернуть» --------------------------------------------------------------------------------------------

    [Fact]
    public void Declining_a_linked_climb_removes_the_link_Undo_restores_it_and_Вернуть_brings_the_mark_back_without_it()
    {
        Propose();
        var clip = MarkedClip(0);
        var markup = clip.HoldMarkup;
        var linked = _time.Tick();
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var declined = _time.Tick();
        Assert.True(_svc.DeclineClimb(_a).Success);

        Assert.DoesNotContain(_training.Marks, m => m.Climb == _a);
        var entry = Assert.Single(_training.DeclinedClimbs);
        Assert.Equal((null, declined), (entry.FormerMark!.LinkedClipId, entry.FormerMark.PositionChangedAt));
        Assert.Same(markup, clip.HoldMarkup);                              // the animation unchanged
        Assert.Equal(Messages.FitMark_Declined, _svc.LinkFitClimb(_a, clip.Id).Message);

        _f.UndoRedo.Undo();
        Assert.Equal((clip.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
        Assert.Empty(_training.DeclinedClimbs);
        _f.UndoRedo.Redo();
        Assert.True(_svc.RestoreClimb(_a).Success);
        Assert.Equal((null, declined, FitMarkPosition.Proposed), (Mark(_a).LinkedClipId, Mark(_a).PositionChangedAt, Mark(_a).Position));
        Assert.Equal(Messages.FitMark_NotDeclined, _svc.RestoreClimb(_a).Message);
    }

    [Fact]
    public void A_new_Z_never_brings_a_declined_climb_back_and_a_climb_declined_without_a_mark_gets_a_proposal_on_restore()
    {
        Assert.True(_svc.DeclineClimb(_a).Success);                        // no Z yet: no mark
        Assert.Null(Assert.Single(_training.DeclinedClimbs).FormerMark);
        Propose();
        Assert.DoesNotContain(_training.Marks, m => m.Climb == _a);
        Assert.True(_svc.RestoreClimb(_a).Success);
        Assert.Equal(FitMarkPosition.Proposed, Mark(_a).Position);
    }

    // ---- The link following its markup ---------------------------------------------------------------------------------

    [Fact]
    public void Deleting_the_linked_clip_removes_the_link_in_the_same_step()
    {
        Propose();
        var clip = MarkedClip(0);
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var linked = Mark(_a).PositionChangedAt;
        var deleted = _time.Tick();
        Assert.True(_svc.DeleteClips([clip.Id]).Success);
        Assert.Equal((null, deleted), (Mark(_a).LinkedClipId, Mark(_a).PositionChangedAt));
        _f.UndoRedo.Undo();
        Assert.Equal((clip.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
    }

    [Fact]
    public void A_split_before_the_animation_moves_the_link_to_the_right_part_inside_it_removes_it_after_it_keeps_it()
    {
        Propose();
        var clip = MarkedClip(0);                                          // the orange ends at frame 200
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var linked = Mark(_a).PositionChangedAt;
        _time.Tick();

        Assert.True(_svc.Split(MediaTime.FromFrame(20, Rate), [clip.Id]).Success);           // before As
        var right = _f.V1.Clips.OfType<VideoClip>().Single(c => c.HoldMarkup?.Animation is not null);
        Assert.NotEqual(clip.Id, right.Id);
        Assert.Equal((right.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
        _f.UndoRedo.Undo();
        Assert.Equal(clip.Id, Mark(_a).LinkedClipId);

        Assert.True(_svc.Split(MediaTime.FromFrame(190, Rate), [clip.Id]).Success);          // inside [As, Ae)
        Assert.Null(Mark(_a).LinkedClipId);
        _f.UndoRedo.Undo();

        Assert.True(_svc.Split(MediaTime.FromFrame(300, Rate), [clip.Id]).Success);          // after Ae
        Assert.Equal((clip.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
    }

    [Fact]
    public void Moving_T_of_the_linked_animation_sets_the_position_time()
    {
        Propose();
        var clip = MarkedClip(0);
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var moved = _time.Tick();
        Assert.True(_svc.LayOutIntroAndFinal(clip.Id, MediaTime.FromFrame(250, Rate)).Success);
        Assert.Equal((clip.Id, moved), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
        Assert.Equal(MediaTime.FromFrame(250, Rate), FitMarkRules.EffectiveStart(Mark(_a), _f.Project)!.Value.SourceTime);
    }

    [Fact]
    public void Spinning_the_linked_animations_length_stays_one_undo_step_with_the_marks()
    {
        Propose();
        var clip = MarkedClip(0);
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var linked = Mark(_a).PositionChangedAt;
        var animation = clip.HoldMarkup!.Animation!;
        var spun = _time.Tick();
        Assert.True(_svc.SetHoldAnimation(clip.Id, animation with { Duration = animation.Duration + MediaTime.FromFrame(5, Rate) }).Success);
        Assert.True(_svc.SetHoldAnimation(clip.Id, animation with { Duration = animation.Duration + MediaTime.FromFrame(10, Rate) }).Success);
        Assert.Equal(spun, Mark(_a).PositionChangedAt);                    // T moved

        _f.UndoRedo.Undo();                                                // one step for both
        Assert.Equal(animation, clip.HoldMarkup!.Animation);
        Assert.Equal((clip.Id, linked), (Mark(_a).LinkedClipId!.Value, Mark(_a).PositionChangedAt));
        _f.UndoRedo.Redo();
        Assert.Equal(spun, Mark(_a).PositionChangedAt);
    }

    // ---- Media removal (audit) --------------------------------------------------------------------------------------------

    [Fact]
    public void Removing_the_video_takes_its_marks_with_it_the_project_still_opens_and_Undo_restores_them()
    {
        Propose();
        Assert.True(_svc.MoveFitMark(_b, MediaTime.FromSeconds(1)).Success);
        Assert.True(_svc.DeclineClimb(_b).Success);                        // a declined climb with a former mark on the video
        var marks = _training.Marks.ToList();
        var declined = _training.DeclinedClimbs.ToList();

        Assert.True(_svc.RemoveMedia(_video.Id).Success);
        Assert.Empty(_training.Marks);
        Assert.Null(Assert.Single(_training.DeclinedClimbs).FormerMark);    // still declined, its mark gone with the video

        // No mark refers to a video that isn't in the project: the saved file opens (it was refused as damaged before).
        var folder = Path.Combine(Path.GetTempPath(), "aive-fit-remove", Guid.NewGuid().ToString("N"));
        var json = AiVideoEditor.Project.Persistence.ProjectSerializer.Serialize(_f.Project, folder);
        var reopened = AiVideoEditor.Project.Persistence.ProjectSerializer.Deserialize(json, folder, _ => true);
        Assert.Empty(reopened.Training!.Marks);

        _f.UndoRedo.Undo();
        Assert.Contains(_video, _f.Project.MediaAssets);
        Assert.Equal(marks, _training.Marks);
        Assert.Equal(declined, _training.DeclinedClimbs);
        _f.UndoRedo.Redo();
        Assert.Empty(_training.Marks);
    }

    // ---- The paste refusal (B1, О1) -------------------------------------------------------------------------------------

    [Fact]
    public void Paste_and_Duplicate_of_a_linked_clip_are_refused_while_it_is_on_the_timeline()
    {
        Propose();
        var clip = MarkedClip(0);
        var ordinary = MarkedClip(600);
        Assert.True(_svc.LinkFitClimb(_a, clip.Id).Success);
        var count = _f.V1.Clips.Count;

        var copied = _svc.CopyClips([clip.Id])!;
        var refused = _svc.PasteClips(copied, MediaTime.FromFrame(2000, Rate));
        Assert.False(refused.Success);
        Assert.Equal(string.Format(Messages.Edit_PasteDuplicateFitMarkup, Messages.Verb_Paste), refused.Message);
        Assert.False(_svc.DuplicateClips([clip.Id]).Success);
        Assert.Equal(count, _f.V1.Clips.Count);

        Assert.True(_svc.PasteClips(_svc.CopyClips([ordinary.Id])!, MediaTime.FromFrame(2000, Rate)).Success);   // an ordinary markup

        Assert.True(_svc.DeleteClips([clip.Id]).Success);                  // the original gone: pasted, unlinked
        var pasted = _svc.PasteClips(copied, MediaTime.FromFrame(3000, Rate));
        Assert.True(pasted.Success);
        Assert.DoesNotContain(_training.Marks, m => m.LinkedClipId is { } id && pasted.ClipIds.Contains(id));
    }

    [Fact]
    public void Without_a_training_the_D048_edits_work_as_before()
    {
        _f.Projects.SetTraining(null);
        var clip = MarkedClip(0);
        Assert.True(_svc.Split(MediaTime.FromFrame(20, Rate), [clip.Id]).Success);
        Assert.True(_svc.DuplicateClips([_f.V1.Clips.Last().Id]).Success);
        Assert.Equal(Messages.Training_None, _svc.DeclineClimb(_a).Message);
    }
}
