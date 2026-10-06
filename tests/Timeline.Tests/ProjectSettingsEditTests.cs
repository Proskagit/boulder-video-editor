using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 13 Step 13.6 (D028): <see cref="ITimelineEditService.SetProjectSettings"/> — the canvas and the frame rate
/// together as one Undo step with every rule of 13.4 and 13.5, both parts checked before the first change; a part that
/// doesn't change goes to the other one's own method; nothing changing is <see cref="TimelineEditResult.NoChange"/>.
/// </summary>
public class ProjectSettingsEditTests
{
    private static readonly FrameRate Fps25 = FrameRate.Fps25;
    private static readonly FrameRate Fps24 = FrameRate.Fps24;

    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;
    private static MediaTime At(long frame, FrameRate rate) => MediaTime.FromFrame(frame, rate);
    private static void MarkSaved(TimelineFixture f) => f.UndoRedo.MarkSavePoint(f.UndoRedo.CurrentPosition);

    /// <summary>A locked 25 fps, 1920 × 1080 project: a video [0, 50) moved left; a text [1, 4) on V2 moved down with a
    /// stored fade in of 10 frames (longer than the clip, as a file may hold); the playhead at frame 7.</summary>
    private static (TimelineFixture F, VideoClip Video, TextClip Title) Scene()
    {
        var f = new TimelineFixture();
        var media = f.AddAsset("clip.mp4", MediaKind.Video,
            new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Fps25, Width = 1920, Height = 1080 });
        Ok(f.Service.AddClip(media.Id, f.V1.Id, MediaTime.Zero));
        Ok(f.Service.TrimClip(f.V1.Clips[0].Id, ClipEdge.End, At(50, Fps25)));
        var video = (VideoClip)f.V1.Clips[0];
        Ok(f.Service.SetClipProperties(video.Id, new ClipPropertyChange { Visual = VisualProperties.Of(video)!.Value with { PositionX = -400 } }));
        Ok(f.Service.AddTrack(TrackType.Video));
        var v2 = f.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First();
        Ok(f.Service.AddTextClip(At(1, Fps25)));
        var title = (TextClip)v2.Clips.Single();
        Ok(f.Service.TrimClip(title.Id, ClipEdge.End, At(4, Fps25)));
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange { Visual = VisualProperties.Of(title)!.Value with { PositionY = 300 } }));
        title.FadeIn = At(10, Fps25);
        f.Project.Timeline.PlayheadPosition = At(7, Fps25);
        MarkSaved(f);
        return (f, video, title);
    }

    [Fact]
    public void Canvas_and_rate_together_are_one_undo_step_with_both_rules_and_one_notification()
    {
        var (f, video, title) = Scene();
        var before = f.Snapshot();
        var changes = f.TimelineChangedCount;

        var result = f.Service.SetProjectSettings(3840, 2160, Fps24);

        Ok(result);
        Assert.Equal("Change Project Settings", Top(f));
        Assert.Contains("3840 × 2160", result.Message);
        Assert.Contains("24 FPS", result.Message);
        Assert.Equal((3840, 2160, Fps24, true), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        Assert.Equal(-800.0, video.PositionX);                                  // 13.4: × 2
        Assert.Equal((600.0, 96.0), (title.PositionY, title.FontSize));
        Assert.Equal(At(48, Fps24), video.TimelineEnd);                         // 13.5: 2 s → 48 frames
        Assert.Equal((At(1, Fps24), At(4, Fps24)), (title.TimelineStart, title.TimelineEnd)); // 0.04 s → 1; 0.16 s → 3.84 → 4
        Assert.Equal(At(3, Fps24), title.FadeIn);                               // the re-grid cut the fade to the clip …
        Assert.Equal(At(7, Fps24), f.Project.Timeline.PlayheadPosition);        // 0.28 s → 6.72 → 7
        Assert.Equal(changes + 1, f.TimelineChangedCount);
        Assert.True(f.Project.IsDirty);
        f.AssertValid();
        var after = f.Snapshot();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
        Assert.Equal((1920, 1080, Fps25), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate));
        Assert.Equal(-400.0, video.PositionX);
        Assert.Equal((300.0, 48.0), (title.PositionY, title.FontSize));
        Assert.Equal(At(10, Fps25), title.FadeIn);                              // … and Undo brings the stored one back
        Assert.Equal(At(7, Fps25), f.Project.Timeline.PlayheadPosition);
        Assert.False(f.Project.IsDirty);
        Assert.Equal(changes + 2, f.TimelineChangedCount);

        f.UndoRedo.Redo();
        Assert.Equal(after, f.Snapshot());
        Assert.Equal((3840, 2160, Fps24), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate));
        Assert.Equal(-800.0, video.PositionX);                                  // the canvas part never wrote a fade back …
        Assert.Equal((600.0, 96.0), (title.PositionY, title.FontSize));
        Assert.Equal(At(3, Fps24), title.FadeIn);                               // … and the rate part never a position
        Assert.Equal(changes + 3, f.TimelineChangedCount);
    }

    [Fact]
    public void Only_the_canvas_goes_to_SetCanvasSize()
    {
        var (f, video, _) = Scene();

        Ok(f.Service.SetProjectSettings(1080, 1920, Fps25));

        Assert.Equal("Set Frame Size", Top(f));
        Assert.Equal((1080, 1920, Fps25), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate));
        Assert.Equal(-400 * 0.5625, video.PositionX);
    }

    [Fact]
    public void Only_the_rate_goes_to_SetFrameRate()
    {
        var (f, video, _) = Scene();

        Ok(f.Service.SetProjectSettings(1920, 1080, Fps24));

        Assert.Equal("Set Frame Rate", Top(f));
        Assert.Equal(-400.0, video.PositionX);
        Assert.Equal(Fps24, f.Settings.FrameRate);
    }

    [Fact]
    public void Nothing_changing_is_no_change_and_no_undo_step()
    {
        var (f, _, _) = Scene();
        var (top, changes) = (f.UndoRedo.CurrentPosition, f.TimelineChangedCount);

        var result = f.Service.SetProjectSettings(1920, 1080, Fps25);

        Assert.True(result.Success && result.NoChange);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }

    [Fact]
    public void The_provisional_rate_with_a_new_canvas_locks_it_in_the_same_step()
    {
        var f = new TimelineFixture();
        Ok(f.Service.AddTextClip(MediaTime.Zero));
        MarkSaved(f);
        Assert.False(f.Settings.IsFrameRateLocked);

        Ok(f.Service.SetProjectSettings(1080, 1080, FrameRate.Default));

        Assert.Equal("Change Project Settings", Top(f));
        Assert.Equal((1080, 1080, FrameRate.Default, true), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        f.UndoRedo.Undo();
        Assert.Equal((1920, 1080, false), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.IsFrameRateLocked));
        Assert.False(f.Project.IsDirty);
    }

    // --- atomic refusal: every part checked before the first change ----------------------------------------------------

    [Fact]
    public void A_refused_re_grid_refuses_the_canvas_part_too()
    {
        var (f, video, title) = Scene();
        var image = f.Image("still.png");
        var v3Result = f.Service.AddTrack(TrackType.Video);
        Ok(v3Result);
        var v3 = f.Project.Timeline.VideoTracks.OrderByDescending(t => t.Order).First();
        // 60 fps project with a one-frame clip between neighbours: no room at 24 fps.
        f.Settings.FrameRate = FrameRate.Fps60;
        foreach (var track in f.Project.Timeline.VideoTracks.Concat(f.Project.Timeline.AudioTracks))
            if (track != v3) { track.Clips.Clear(); track.Transitions.Clear(); }
        f.PlaceImageFrames(v3, image, 0, 10, FrameRate.Fps60);
        f.PlaceImageFrames(v3, image, 10, 11, FrameRate.Fps60);
        f.PlaceImageFrames(v3, image, 11, 20, FrameRate.Fps60);
        MarkSaved(f);

        AssertRefused(f, () => f.Service.SetProjectSettings(3840, 2160, Fps24), "no room on the new frame grid");
        Assert.True(video is not null && title is not null);
    }

    [Fact]
    public void A_scaled_value_out_of_limits_refuses_the_rate_part_too()
    {
        // The rate part alone would succeed and is planned first; the canvas part (a font size 600 → 1200) refuses, and
        // nothing — not the rate, not a clip's timing — has changed.
        var (f, _, title) = Scene();
        Ok(f.Service.SetClipProperties(title.Id, new ClipPropertyChange { Text = TextProperties.Of(title)!.Value with { FontSize = 600 } }));
        MarkSaved(f);

        AssertRefused(f, () => f.Service.SetProjectSettings(3840, 2160, Fps24), "Font size");
    }

    [Theory]
    [InlineData(1921, 1080, 24, 1, "even")]
    [InlineData(32, 1080, 24, 1, "between 64 and 4096")]
    [InlineData(1080, 1920, 15, 1, "not one of the project frame rates")]
    [InlineData(1080, 1920, 120, 1, "not one of the project frame rates")]
    public void An_invalid_part_refuses_the_whole_change(int width, int height, int numerator, int denominator, string reason)
    {
        var (f, _, _) = Scene();
        AssertRefused(f, () => f.Service.SetProjectSettings(width, height, new FrameRate(numerator, denominator)), reason);
    }

    private static void AssertRefused(TimelineFixture f, Func<TimelineEditResult> act, string reason)
    {
        var (before, top, changes, playhead) = (f.Snapshot(), f.UndoRedo.CurrentPosition, f.TimelineChangedCount, f.Project.Timeline.PlayheadPosition);
        var (width, height, rate, locked) = (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate, f.Settings.IsFrameRateLocked);
        var values = f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Select(c => (VisualProperties.Of(c), TextProperties.Of(c))).ToList();

        var result = act();

        Assert.False(result.Success);
        Assert.Contains(reason, result.Message);
        Assert.Equal((width, height, rate, locked), (f.Settings.FrameWidth, f.Settings.FrameHeight, f.Settings.FrameRate, f.Settings.IsFrameRateLocked));
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(values, f.Project.Timeline.VideoTracks.SelectMany(t => t.Clips).Select(c => (VisualProperties.Of(c), TextProperties.Of(c))).ToList());
        Assert.Equal(playhead, f.Project.Timeline.PlayheadPosition);
        Assert.Same(top, f.UndoRedo.CurrentPosition);
        Assert.Equal(changes, f.TimelineChangedCount);
        Assert.False(f.Project.IsDirty);
    }
}
