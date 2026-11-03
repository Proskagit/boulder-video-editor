using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>
/// D051 §4.5, §5.5.1, §5.8 (T4, T5): the choice of the video and the position of a proposal on every scenario of the plan
/// (one video, overlaps, equal starts, lengths, a joint, a gap, a negative estimate, nothing filmed), the proposals of a
/// training, the 1 : 1 model of the watch time (§1.5 — the test fails on purpose if a conversion is ever added), the
/// usable marks and the observations, also of a link to an animation on another video.
/// </summary>
public class FitMarkRulesTests
{
    private const long S = TimeSpan.TicksPerSecond;
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    internal static WorkoutSegment Climb(int index, DateTimeOffset start, double seconds = 30) =>
        new(index, WorkoutSegmentKind.Climb, index + 1, start, start.AddSeconds(Math.Ceiling(seconds)), MediaTime.FromSeconds(seconds), null, null);

    internal static ClimbingWorkout Workout(params WorkoutSegment[] climbs) => new()
    {
        DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(2), UtcOffset = null,
        ElapsedTime = MediaTime.FromSeconds(7500), TimerTime = MediaTime.FromSeconds(7500), Sport = 31, SubSport = 69,
        AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = climbs, Warnings = []
    };

    private static FitVideo Video(int seconds, DateTimeOffset zero, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), zero, Rate, seconds * 25L);

    private static (Guid, FitMarkPosition)? Choose(WorkoutSegment climb, long correctionSeconds, params FitVideo[] videos) =>
        FitMarkRules.ChooseVideo(climb, climb.StartUtc.AddTicks(-correctionSeconds * S), MediaTime.Zero, videos);

    // ---- §5.5.1 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void One_video_holding_W_auto_gives_a_proposed_mark()
    {
        var v = Video(60, W0);
        Assert.Equal((v.AssetId, FitMarkPosition.Proposed), Choose(Climb(0, W0.AddSeconds(30)), 12, v));
    }

    [Fact]
    public void Of_two_overlapping_recordings_holding_the_whole_climb_the_earlier_one_wins()
    {
        var early = Video(120, W0);
        var late = Video(120, W0.AddSeconds(10));
        Assert.Equal(early.AssetId, Choose(Climb(0, W0.AddSeconds(40), 20), 12, late, early)!.Value.Item1);
    }

    [Fact]
    public void A_recording_holding_the_whole_climb_wins_over_one_that_stops_before_its_end()
    {
        var stops = Video(50, W0);                          // ends at +50 s, the climb at +40…+60 s
        var whole = Video(120, W0.AddSeconds(20));
        Assert.Equal(whole.AssetId, Choose(Climb(0, W0.AddSeconds(40), 20), 12, stops, whole)!.Value.Item1);
    }

    [Fact]
    public void With_equal_starts_the_longer_recording_wins_then_the_smaller_media_id()
    {
        var shortOne = Video(100, W0);
        var longOne = Video(200, W0);
        Assert.Equal(longOne.AssetId, Choose(Climb(0, W0.AddSeconds(40), 20), 12, shortOne, longOne)!.Value.Item1);

        var a = Video(100, W0, new Guid("00000000-0000-0000-0000-000000000002"));
        var b = Video(100, W0, new Guid("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(b.AssetId, Choose(Climb(0, W0.AddSeconds(40), 20), 12, a, b)!.Value.Item1);
        Assert.Equal(b.AssetId, Choose(Climb(0, W0.AddSeconds(40), 20), 12, b, a)!.Value.Item1);   // the order does not matter
    }

    [Fact]
    public void At_a_joint_the_video_holding_W_auto_wins_proposed()
    {
        var v1 = Video(60, W0);                             // to +60 s
        var v2 = Video(60, W0.AddSeconds(60));
        Assert.Equal((v1.AssetId, FitMarkPosition.Proposed), Choose(Climb(0, W0.AddSeconds(65)), 12, v1, v2));   // W_auto = +53 s
    }

    [Fact]
    public void W_auto_in_a_gap_or_before_the_recording_clamps_to_the_first_frame()
    {
        var v1 = Video(30, W0);                             // to +30 s
        var v2 = Video(60, W0.AddSeconds(40));
        Assert.Equal((v2.AssetId, FitMarkPosition.Clamped(MediaTime.Zero)), Choose(Climb(0, W0.AddSeconds(45)), 12, v1, v2));   // W_auto +33 s
        Assert.Equal((v2.AssetId, FitMarkPosition.Clamped(MediaTime.Zero)), Choose(Climb(1, W0.AddSeconds(42)), 12, v2));
    }

    [Fact]
    public void A_negative_estimate_with_W_auto_after_the_recording_clamps_to_the_start_of_the_last_frame()
    {
        var v = Video(60, W0);
        Assert.Equal((v.AssetId, FitMarkPosition.Clamped(MediaTime.FromFrame(1499, Rate))), Choose(Climb(0, W0.AddSeconds(50), 5), -20, v));
    }

    [Fact]
    public void A_climb_whose_start_is_not_filmed_gets_no_mark()
    {
        var v = Video(60, W0.AddSeconds(100));
        Assert.Null(Choose(Climb(0, W0.AddSeconds(30)), 12, v));                       // before the first video (C1 of 27.09)
        Assert.Null(Choose(Climb(1, W0.AddSeconds(95), 30), 12, v));                  // begun before the recording
        Assert.Null(Choose(Climb(2, W0.AddSeconds(30)), 12));                          // no video with Z
    }

    [Fact]
    public void Only_videos_with_a_zero_and_a_frame_are_candidates()
    {
        var withZero = new MediaAsset { FilePath = "a.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = W0,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Rate } };
        var noZero = new MediaAsset { FilePath = "b.mp4", Kind = MediaKind.Video, Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10) } };
        var noFrames = new MediaAsset { FilePath = "c.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = W0,
            Metadata = new MediaMetadata { Duration = new MediaTime(1), FrameRate = Rate } };
        var audio = new MediaAsset { FilePath = "d.mp3", Kind = MediaKind.Audio, RecordingZeroOverrideUtc = W0,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10) } };
        var videos = FitMarkRules.Videos([withZero, noZero, noFrames, audio], Rate);
        Assert.Equal([withZero.Id], videos.Select(v => v.AssetId));
        Assert.Equal(250, videos[0].Frames);
        Assert.Equal([withZero.Id, noZero.Id], FitMarkRules.Videos([withZero, noZero], Rate, (noZero.Id, W0)).Select(v => v.AssetId));
    }

    // ---- Proposals (§5.5) ----------------------------------------------------------------------------------------------

    private static TrainingReference Training(params WorkoutSegment[] climbs) => new()
    {
        FilePath = "a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
        Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(2), 294, 814),
        Workout = Workout(climbs), Availability = TrainingAvailability.Available
    };

    [Fact]
    public void Proposals_skip_active_and_declined_climbs_and_freeze_the_estimate()
    {
        var climbs = new[] { Climb(0, W0.AddSeconds(20)), Climb(2, W0.AddSeconds(40)), Climb(4, W0.AddSeconds(60)), Climb(6, W0.AddSeconds(500)) };
        var training = Training(climbs);
        var v = Video(120, W0);
        training.Marks.Add(new FitMark(FitMarkRules.IdentityOf(climbs[1]), W0, 1, 0, v.AssetId, FitMarkPosition.Proposed, W0));
        training.DeclinedClimbs.Add(new DeclinedClimb(FitMarkRules.IdentityOf(climbs[2])));
        var now = W0.AddDays(1);

        var marks = FitMarkRules.Proposals(training, [v], 13 * S, 7, now);
        var mark = Assert.Single(marks);                                  // climb 3 is not filmed
        Assert.Equal(new FitMark(FitMarkRules.IdentityOf(climbs[0]), W0.AddSeconds(7), 13 * S, 7, v.AssetId, FitMarkPosition.Proposed, now), mark);
        Assert.Empty(FitMarkRules.Proposals(training, [v], 13 * S, 7, now, onlyVideo: Guid.NewGuid()));
        Assert.Empty(FitMarkRules.Proposals(new TrainingReference { FilePath = "x", FileSizeBytes = 0, Sha256 = "", Activity = training.Activity }, [v], 12 * S, 0, now));
    }

    // ---- §4.5 and §5.8 ---------------------------------------------------------------------------------------------------

    private static Project ProjectWith(TrainingReference training, out MediaAsset v, out MediaAsset u, DateTimeOffset zeroV, DateTimeOffset zeroU)
    {
        var project = new Project { Settings = new ProjectSettings { FrameRate = Rate } };
        v = new MediaAsset { FilePath = "v.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = zeroV,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate } };
        u = new MediaAsset { FilePath = "u.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = zeroU,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate } };
        project.MediaAssets.Add(v);
        project.MediaAssets.Add(u);
        project.Training = training;
        return project;
    }

    [Fact]
    public void The_watch_time_is_the_one_to_one_model_of_D049()
    {
        // D051 §1.5: w = Z + s + ClockCorrection, no conversion of the source time — this test fails if one is ever added.
        var climb = Climb(0, W0.AddSeconds(30));
        var training = Training(climb);
        training.ClockCorrection = new MediaTime(3 * S);
        var project = ProjectWith(training, out var v, out _, W0, W0);
        var moved = new FitMark(FitMarkRules.IdentityOf(climb), W0, 12 * S, 0, v.Id, FitMarkPosition.Moved(new MediaTime(10 * S + 7)), W0);
        Assert.Equal(W0.AddTicks(10 * S + 7 + 3 * S), FitMarkRules.WatchTimeOfStart(moved, project));
        var proposed = moved with { Position = FitMarkPosition.Proposed, AutoStartUtc = W0.AddSeconds(18) };
        Assert.Equal(W0.AddSeconds(18), FitMarkRules.WatchTimeOfStart(proposed, project));
        Assert.Equal(new MediaTime(15 * S), FitMarkRules.EffectiveStart(proposed, project)!.Value.SourceTime);   // 18 − 3 − 0
    }

    [Fact]
    public void Clamped_marks_without_a_link_are_not_usable_and_a_proposed_one_outside_its_video_still_is()
    {
        var climbs = new[] { Climb(0, W0.AddSeconds(30)), Climb(2, W0.AddSeconds(60)), Climb(4, W0.AddSeconds(90)) };
        var training = Training(climbs);
        var project = ProjectWith(training, out var v, out _, W0, W0);
        training.Marks.Add(new FitMark(FitMarkRules.IdentityOf(climbs[0]), W0.AddSeconds(18), 12 * S, 0, v.Id, FitMarkPosition.Clamped(MediaTime.Zero), W0));
        training.Marks.Add(new FitMark(FitMarkRules.IdentityOf(climbs[1]), W0.AddSeconds(48), 12 * S, 0, v.Id, FitMarkPosition.Moved(new MediaTime(40 * S)), W0));
        // W_auto 500 s after the video's end: not drawn, but usable with c = its StartCorrection (D051 §5.8).
        training.Marks.Add(new FitMark(FitMarkRules.IdentityOf(climbs[2]), W0.AddSeconds(78), 12 * S, 0, v.Id, FitMarkPosition.Proposed, W0) with
        {
            AutoStartUtc = W0.AddSeconds(78)
        });
        v.RecordingZeroOverrideUtc = W0.AddSeconds(-500);

        var observations = FitMarkRules.Observations(project);
        Assert.Equal(2, observations.Count);
        Assert.DoesNotContain(observations, o => o.Key.Climb == FitMarkRules.IdentityOf(climbs[0]));
        Assert.Equal(12 * S, observations.Single(o => o.Key.Climb == FitMarkRules.IdentityOf(climbs[2])).Value);
        Assert.Equal(60 * S - (40 * S - 500 * S), observations.Single(o => o.Key.Climb == FitMarkRules.IdentityOf(climbs[1])).Value);

        v.RecordingZeroOverrideUtc = null;                                  // no Z on the effective start's video: no value
        Assert.All(FitMarkRules.Observations(project), o => Assert.Null(o.Value));
    }

    [Fact]
    public void A_link_to_an_animation_on_another_video_uses_that_videos_zero_once()
    {
        var climb = Climb(0, W0.AddSeconds(60));
        var training = Training(climb);
        training.ClockCorrection = new MediaTime(2 * S);
        var project = ProjectWith(training, out var v, out var u, W0, W0.AddSeconds(10));
        var clip = new VideoClip
        {
            MediaAssetId = u.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(60), SourceIn = MediaTime.FromSeconds(20),
            SourceOut = MediaTime.FromSeconds(80), Speed = ClipSpeed.Normal,
            HoldMarkup = new HoldMarkup { Animation = new HoldAnimation { Mode = HoldAnimationMode.Duration, StartSourceTime = MediaTime.FromSeconds(30), Duration = MediaTime.FromSeconds(5) } }
        };
        var track = new Track { Type = TrackType.Video };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        var mark = new FitMark(FitMarkRules.IdentityOf(climb), W0.AddSeconds(48), 12 * S, 0, v.Id, FitMarkPosition.Clamped(MediaTime.Zero), W0, clip.Id);
        training.Marks.Add(mark);

        // T = 35 s of u's source; W = Z_u + 35 s + CC = W0 + 10 + 35 + 2 = W0 + 47 s; c = 60 − 47 = 13 s.
        Assert.Equal(new FitEffectiveStart(u.Id, MediaTime.FromSeconds(35), null), FitMarkRules.EffectiveStart(mark, project));
        Assert.Equal(13 * S, Assert.Single(FitMarkRules.Observations(project)).Value);
        v.RecordingZeroOverrideUtc = W0.AddSeconds(-30);                   // the mark's video: no influence
        Assert.Equal(13 * S, Assert.Single(FitMarkRules.Observations(project)).Value);
        u.RecordingZeroOverrideUtc = W0.AddSeconds(11);                    // the animation's video: the value moves
        Assert.Equal(12 * S, Assert.Single(FitMarkRules.Observations(project)).Value);
        u.RecordingZeroOverrideUtc = null;
        Assert.Null(Assert.Single(FitMarkRules.Observations(project)).Value);
    }

    [Fact]
    public void The_same_start_is_decided_by_the_video_and_its_source_frame()
    {
        var project = new Project { Settings = new ProjectSettings { FrameRate = Rate } };
        var id = Guid.NewGuid();
        var frame10 = MediaTime.FromFrame(10, Rate);
        Assert.True(FitMarkRules.SameStart(new FitEffectiveStart(id, frame10, null), new FitEffectiveStart(id, frame10 + new MediaTime(100), null), project));
        Assert.False(FitMarkRules.SameStart(new FitEffectiveStart(id, frame10, null), new FitEffectiveStart(id, MediaTime.FromFrame(11, Rate), null), project));
        Assert.False(FitMarkRules.SameStart(new FitEffectiveStart(id, frame10, null), new FitEffectiveStart(Guid.NewGuid(), frame10, null), project));
        Assert.True(FitMarkRules.SameStart(new FitEffectiveStart(id, null, W0), new FitEffectiveStart(id, null, W0), project));
        Assert.False(FitMarkRules.SameStart(new FitEffectiveStart(id, null, W0), new FitEffectiveStart(id, frame10, null), project));
    }
    [Fact]
    public void The_marks_are_never_in_Preview_or_the_export()
    {
        // D051 §8.1: the composition (Preview = export) of a project is the same with and without FIT marks.
        var climb = Climb(0, W0.AddSeconds(30));
        var training = Training(climb);
        var project = ProjectWith(training, out var v, out _, W0, W0);
        project.Settings.FrameWidth = 1920;
        project.Settings.FrameHeight = 1080;
        var clip = new VideoClip { MediaAssetId = v.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromSeconds(60),
            SourceIn = MediaTime.Zero, SourceOut = MediaTime.FromSeconds(60) };
        var track = new Track { Type = TrackType.Video };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        var without = Enumerable.Range(0, 60).Select(s => PlaybackSnapshotBuilder.Build(project, 1).LayersAt(MediaTime.FromSeconds(s)).Length).ToList();
        training.Marks.Add(new FitMark(FitMarkRules.IdentityOf(climb), W0.AddSeconds(18), 12 * S, 0, v.Id, FitMarkPosition.Proposed, W0, clip.Id));
        var with = Enumerable.Range(0, 60).Select(s => PlaybackSnapshotBuilder.Build(project, 1).LayersAt(MediaTime.FromSeconds(s)).Length).ToList();
        Assert.Equal(without, with);
    }
}
