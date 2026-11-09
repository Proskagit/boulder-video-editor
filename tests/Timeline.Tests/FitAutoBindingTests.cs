using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// D054: the FIT's climbs bound to the video clips without any choice of a climb or a route — by each video's time (its
/// <c>Z</c>, else the file's recording-time tag read as the end, else the file-name time under the watch's offset). At
/// 25 fps, the watch at UTC+3, W0 = 10:00:00 UTC, the start correction 12 s: video A (120 s) starts at W0 by its tag,
/// video B (60 s) at W0 + 180 s by its file name. Climb 1 is FIT [W0 + 30, W0 + 50] (estimated [18, 45.4] s on A), climb
/// 2 FIT [W0 + 200, W0 + 220] ([8, 35.4] s on B), climb 3 FIT [W0 + 400, W0 + 420] — in no video.
/// </summary>
public sealed class FitAutoBindingTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset W0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TimelineFixture _f = new();

    private static long F(MediaTime t) => t.ToNearestFrame(Rate);

    private MediaAsset VideoA() => Tagged(_f.Video(120, Rate, "a.mp4"), W0.AddSeconds(120));     // tag = the end

    private MediaAsset VideoB() => _f.Video(60, Rate, "video_20261103_130300.mp4");              // 13:03 local = W0 + 180 s

    private static MediaAsset Tagged(MediaAsset asset, DateTimeOffset end)
    {
        asset.Metadata!.RecordingTimeProbe = RecordingTimeProbe.Found(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"), RecordingTimeTagOrigin.Format);
        return asset;
    }

    private VideoClip Add(MediaAsset asset, double? atSeconds = null)
    {
        Assert.True(_f.Service.AddClip(asset.Id, null, atSeconds is { } s ? MediaTime.FromSeconds(s) : null).Success);
        return _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Last(c => c.MediaAssetId == asset.Id);
    }

    private static WorkoutSegment Climb(int index, double startSeconds, double seconds) =>
        new(index, WorkoutSegmentKind.Climb, index / 2 + 1, W0.AddSeconds(startSeconds), W0.AddSeconds(startSeconds + seconds),
            MediaTime.FromSeconds(seconds), null, null);

    private TrainingReference Training(params WorkoutSegment[] climbs)
    {
        var training = new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 1, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(W0.AddMinutes(-5), W0.AddHours(1), 294, 814),
            Workout = new ClimbingWorkout
            {
                DeviceName = "COROS", Manufacturer = 294, Product = 814, StartUtc = W0.AddMinutes(-5), EndUtc = W0.AddHours(1),
                UtcOffset = TimeSpan.FromHours(3), ElapsedTime = MediaTime.FromSeconds(3900), TimerTime = MediaTime.FromSeconds(3900),
                Sport = 31, SubSport = 69, AverageHeartRate = null, MaxHeartRate = null, HeartRate = [], Pauses = [], Segments = climbs, Warnings = []
            },
            Availability = TrainingAvailability.Available
        };
        _f.Projects.SetTraining(training);
        return training;
    }

    private TrainingReference ThreeClimbs() => Training(Climb(0, 30, 20), Climb(2, 200, 20), Climb(4, 400, 20));

    private TemplateClip[] Areas() => _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>()
        .Where(t => t.FitClimb is not null).OrderBy(t => t.TimelineStart).ToArray();

    private TemplateClip AreaOf(int climbIndex) => Areas().Single(t => t.FitClimb!.Value.ClimbKey == climbIndex);

    // ---- The import: every clip of the timeline -------------------------------------------------------------------------

    [Fact]
    public void Each_clip_gets_a_green_area_for_each_climb_it_shows_by_the_files_time_without_any_choice()
    {
        var a = Add(VideoA());
        var b = Add(VideoB());
        Assert.Equal(3000, F(b.TimelineStart));
        ThreeClimbs();

        var result = _f.Service.CreateRoutesFromFit();

        Assert.True(result.Success, result.Message);
        var areas = Areas();
        Assert.Equal(2, areas.Length);                                                   // climb 3 is in no video
        // Climb 1 on A: T = 18 s (450) → [T − 7 s, 45.4 + 5 s) = [275, 1260); climb 2 on B: T = 3000 + 200 → [3025, 4010).
        Assert.Equal((a.Id, 275L, 1260L), (AreaOf(0).AnchorClipId!.Value, F(AreaOf(0).TimelineStart), F(AreaOf(0).TimelineEnd)));
        Assert.Equal((b.Id, 3025L, 4010L), (AreaOf(2).AnchorClipId!.Value, F(AreaOf(2).TimelineStart), F(AreaOf(2).TimelineEnd)));
        Assert.All(areas, t => Assert.Equal(BoulderStatus.None, t.Status));
        Assert.NotEqual(AreaOf(0).AnchorRouteId, AreaOf(2).AnchorRouteId);
        Assert.Empty(a.Routes);                                                          // no holds, no orange
        Assert.Contains(string.Format(Messages.FitRoutes_Created, 2), result.Message);
        Assert.Contains(Messages.FitRoutes_Approximate, result.Message);                 // by the files' time
        // The videos' recording zero stays unknown (D050 B1): no marks, no Z.
        Assert.All(_f.Project.MediaAssets, m => Assert.Null(m.RecordingZeroOverrideUtc));
        Assert.Empty(_f.Project.Training!.Marks);

        _f.UndoRedo.Undo();                                                              // one Undo step
        Assert.Empty(Areas());
        _f.UndoRedo.Redo();
        Assert.Equal(2, Areas().Length);
        _f.AssertValid();
    }

    [Fact]
    public void A_second_run_adds_nothing_and_keeps_the_bounds_moved_by_hand()
    {
        Add(VideoA());
        Add(VideoB());
        ThreeClimbs();
        Assert.True(_f.Service.CreateRoutesFromFit().Success);
        var area = AreaOf(0);
        Assert.True(_f.Service.TrimClip(area.Id, ClipEdge.End, MediaTime.FromFrame(1000, Rate)).Success);  // moved by hand

        var again = _f.Service.CreateRoutesFromFit();

        Assert.True(again.NoChange);
        Assert.Equal(Messages.FitRoutes_None, again.Message);
        Assert.Equal(2, Areas().Length);
        Assert.Equal((275L, 1000L), (F(AreaOf(0).TimelineStart), F(AreaOf(0).TimelineEnd)));
    }

    [Fact]
    public void A_climb_split_between_two_clips_goes_to_the_clip_showing_more_and_a_split_area_is_never_doubled()
    {
        var a = Add(VideoA());
        ThreeClimbs();
        Assert.True(_f.Service.Split(MediaTime.FromFrame(600, Rate)).Success);          // 24 s: climb 1 shows 6 s | 21.4 s
        var second = _f.V1.Clips.OfType<VideoClip>().Single(c => c.Id != a.Id);

        Assert.True(_f.Service.CreateRoutesFromFit().Success);
        var area = Assert.Single(Areas());
        Assert.Equal(second.Id, area.AnchorClipId);
        Assert.Equal((600L, 1260L), (F(area.TimelineStart), F(area.TimelineEnd)));     // T clamped to the clip's first frame

        Assert.True(_f.Service.Split(MediaTime.FromFrame(900, Rate), new[] { second.Id, area.Id }).Success);
        Assert.True(_f.Service.CreateRoutesFromFit().NoChange);                          // the climb has its area on this video
        Assert.Equal(2, Areas().Length);                                                 // the split plate, one green
        Assert.Single(Areas().Select(t => t.AnchorRouteId).Distinct());
    }

    [Fact]
    public void Two_videos_of_one_climb_each_get_an_area_and_several_climbs_of_one_clip_each_get_their_own_route()
    {
        var a = Add(VideoA());
        var other = Add(Tagged(_f.Video(120, Rate, "b.mp4"), W0.AddSeconds(125)));       // a second camera, 5 s later
        Training(Climb(0, 30, 20), Climb(2, 80, 20));                                    // both in A, both in the other

        Assert.True(_f.Service.CreateRoutesFromFit().Success);

        Assert.Equal(4, Areas().Length);
        Assert.Equal(2, Areas().Count(t => t.AnchorClipId == a.Id));
        Assert.Equal(2, Areas().Count(t => t.AnchorClipId == other.Id));
        Assert.Equal(4, Areas().Select(t => t.AnchorRouteId).Distinct().Count());
    }

    // ---- What is never done: guessing, a declined climb, a route marked by hand, a locked track --------------------------

    [Fact]
    public void A_video_without_any_time_is_skipped_and_said_never_guessed()
    {
        Add(VideoA());
        var unknown = Add(_f.Video(60, Rate, "clip.mp4"));                               // no Z, no tag, no stamp in the name
        ThreeClimbs();

        var result = _f.Service.CreateRoutesFromFit();
        Assert.True(result.Success);
        Assert.Single(Areas());
        Assert.Contains(string.Format(Messages.FitRoutes_NoTime, 1), result.Message);

        var one = _f.Service.CreateRoutesFromFit(unknown.Id);
        Assert.False(one.Success);
        Assert.Equal(Messages.FitRoutes_ClipNoTime, one.Message);
        Assert.Single(Areas());
    }

    [Fact]
    public void A_file_whose_tag_and_name_disagree_is_skipped_and_said()
    {
        Add(VideoA());
        // Named 13:00:00 local (W0) but its tag says it ended at W0 + 300 s: edited after the recording.
        var edited = Add(Tagged(_f.Video(60, Rate, "video_20261103_130000.mp4"), W0.AddSeconds(300)));
        ThreeClimbs();

        var result = _f.Service.CreateRoutesFromFit();
        Assert.Single(Areas());
        Assert.Contains(string.Format(Messages.FitRoutes_TimesConflict, 1), result.Message);
        var one = _f.Service.CreateRoutesFromFit(edited.Id);
        Assert.False(one.Success);
        Assert.Equal(Messages.FitRoutes_ClipTimesConflict, one.Message);
    }

    [Fact]
    public void A_declined_climb_a_green_made_by_hand_and_a_locked_track_get_no_area()
    {
        var a = Add(VideoA());
        var b = Add(VideoB());
        var training = ThreeClimbs();
        var climb1 = FitMarkRules.IdentityOf(training.Workout!.Climbs.First());
        Assert.True(_f.Service.DeclineClimb(climb1).Success);
        // Climb 2 is marked by hand on B: a route laid out with T inside its green.
        var route = Guid.NewGuid();
        var hold = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.5, 0.4), new NormalizedPoint(0.45, 0.5)),
            RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#E8DCC8"), MediaTime.Zero);
        Assert.True(_f.Service.AddHolds(b.Id, new[] { hold }, route).Success);
        var laidOut = _f.Service.LayOutIntroAndFinal(b.Id, MediaTime.FromFrame(3200, Rate), route);
        Assert.True(laidOut.Success, laidOut.Message);
        var byHand = _f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        var bounds = (byHand.TimelineStart, byHand.TimelineEnd);

        var result = _f.Service.CreateRoutesFromFit();
        Assert.True(result.NoChange);
        Assert.Empty(Areas());
        Assert.Equal(bounds, (byHand.TimelineStart, byHand.TimelineEnd));                // never changed

        Assert.True(_f.Service.RestoreClimb(climb1).Success);
        Assert.True(_f.Service.SetTrackLocked(_f.V1.Id, true).Success);
        var locked = _f.Service.CreateRoutesFromFit();
        Assert.True(locked.NoChange);
        Assert.Contains(string.Format(Messages.FitRoutes_Locked, 1), locked.Message);
        Assert.Empty(Areas());
        _ = a;
    }

    [Fact]
    public void A_recording_zero_set_by_the_user_wins_over_the_files_time_and_is_exact()
    {
        var a = Add(VideoA());
        ThreeClimbs();
        // The user's Z puts A 10 s later than its tag: climb 1 at 8 s.
        Assert.True(_f.Service.SetRecordingZeroOverride(a.MediaAssetId, W0.AddSeconds(10)).Success);   // binds A by itself

        var area = Assert.Single(Areas());
        Assert.Equal((25L, 1010L), (F(area.TimelineStart), F(area.TimelineEnd)));      // T = 8 s → [1 s, 35.4 + 5 s)
        var again = _f.Service.CreateRoutesFromFit();
        Assert.True(again.NoChange);
        Assert.DoesNotContain(Messages.FitRoutes_Approximate, again.Message);
    }

    // ---- «Распознать прохождения из FIT»: one clip --------------------------------------------------------------------------

    [Fact]
    public void A_clip_added_after_the_import_is_bound_by_one_command_on_it_and_only_on_it()
    {
        Add(VideoA());
        ThreeClimbs();
        Assert.True(_f.Service.CreateRoutesFromFit().Success);                           // the import: climb 1 on A
        var later = Add(VideoB());                                                       // added afterwards
        var stray = Add(Tagged(_f.Video(60, Rate, "c.mp4"), W0.AddSeconds(900)));        // shows no climb
        Assert.Single(Areas());

        var result = _f.Service.CreateRoutesFromFit(later.Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal(later.Id, AreaOf(2).AnchorClipId);
        Assert.Equal(2, Areas().Length);

        var again = _f.Service.CreateRoutesFromFit(later.Id);
        Assert.True(again.NoChange);
        Assert.Equal(Messages.FitRoutes_ClipAllMade, again.Message);
        var none = _f.Service.CreateRoutesFromFit(stray.Id);
        Assert.True(none.NoChange);
        Assert.Equal(Messages.FitRoutes_ClipNone, none.Message);

        _f.UndoRedo.Undo();                                                              // the command: one step
        Assert.Single(Areas());
        Assert.False(_f.Service.CreateRoutesFromFit(Guid.NewGuid()).Success);            // not a video clip
    }
}
