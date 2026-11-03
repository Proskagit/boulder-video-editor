using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Phase 35.5: the heart-rate overlay is added like every template — at the playhead on the topmost video track, one undo
/// step — and covers the rest of the video clip right below it (the default length without one); its look, its corner and
/// its heart's colour are one undo step each and validated; Split and Duplicate keep them.
/// </summary>
public class HeartRateOverlayEditTests
{
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);
    private static string? Top(TimelineFixture f) => (f.UndoRedo.CurrentPosition as IUndoableCommand)?.Description;

    private static (TimelineFixture F, Track V2) TwoTracks()
    {
        var f = new TimelineFixture();
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        f.Project.Timeline.VideoTracks.Add(v2);
        return (f, v2);
    }

    private static TemplateClip Add(TimelineFixture f, Track track, long atFrame)
    {
        var result = f.Service.AddTemplateClip(TemplateKind.HeartRate, MediaTime.FromFrame(atFrame, f.Rate));
        Ok(result);
        return (TemplateClip)track.Clips.Single(c => c.Id == result.ClipIds!.Single());
    }

    [Fact]
    public void It_covers_the_rest_of_the_video_below_the_playhead_in_one_undo_step()
    {
        var (f, v2) = TwoTracks();
        var video = f.Video(60, f.Rate);
        var clip = f.Place<VideoClip>(f.V1, video, MediaTime.FromFrame(30, f.Rate).Ticks, MediaTime.FromFrame(30 + 25 * 40, f.Rate).Ticks);
        var before = f.Snapshot();

        var overlay = Add(f, v2, 130);

        Assert.Equal(MediaTime.FromFrame(130, f.Rate), overlay.TimelineStart);
        Assert.Equal(clip.TimelineEnd, overlay.TimelineEnd);
        Assert.Equal(TemplateRules.Defaults(TemplateKind.HeartRate), TemplateProperties.Of(overlay));
        Assert.Equal(MediaTime.FromSeconds(0.3).ToNearestFrame(f.Rate), overlay.FadeIn.ToNearestFrame(f.Rate));
        Assert.Equal("Add Template", Top(f));
        f.AssertValid();

        f.UndoRedo.Undo();
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Without_a_video_below_it_takes_the_default_length()
    {
        var (f, v2) = TwoTracks();
        var overlay = Add(f, v2, 0);
        Assert.Equal(TemplateRules.DefaultDuration(TemplateKind.HeartRate).ToNearestFrame(f.Rate), overlay.Duration.ToNearestFrame(f.Rate));
    }

    [Fact]
    public void The_look_the_corner_and_the_colour_are_one_undo_step_each()
    {
        var (f, v2) = TwoTracks();
        var overlay = Add(f, v2, 0);
        var start = TemplateProperties.Of(overlay)!.Value;

        Ok(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange
            { Template = start with { Look = HeartRateLook.Plain }, Gesture = new ClipPropertyGesture("Change Heart Rate Look") }));
        Ok(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange
            { Template = start with { Look = HeartRateLook.Plain, Corner = OverlayCorner.TopLeft }, Gesture = new ClipPropertyGesture("Move Heart Rate") }));
        Ok(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange
        {
            Template = start with { Look = HeartRateLook.Plain, Corner = OverlayCorner.TopLeft, AccentHex = "#00E676" },
            Gesture = new ClipPropertyGesture("Change Heart Colour")
        }));
        Assert.Equal((HeartRateLook.Plain, OverlayCorner.TopLeft, "#00E676"), (overlay.Look, overlay.Corner, overlay.AccentHex));

        f.UndoRedo.Undo();
        Assert.Equal("#FF1744", overlay.AccentHex);
        f.UndoRedo.Undo();
        Assert.Equal(OverlayCorner.BottomRight, overlay.Corner);
        f.UndoRedo.Undo();
        Assert.Equal(HeartRateLook.Plate, overlay.Look);
        f.UndoRedo.Redo();
        Assert.Equal(HeartRateLook.Plain, overlay.Look);
    }

    [Fact]
    public void An_unknown_look_or_corner_is_refused()
    {
        var (f, v2) = TwoTracks();
        var overlay = Add(f, v2, 0);
        var p = TemplateProperties.Of(overlay)!.Value;
        Assert.False(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange { Template = p with { Look = (HeartRateLook)9 } }).Success);
        Assert.False(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange { Template = p with { Corner = (OverlayCorner)9 } }).Success);
        Assert.Equal(p, TemplateProperties.Of(overlay));
    }

    [Fact]
    public void Split_and_duplicate_keep_the_look_and_the_corner()
    {
        var (f, v2) = TwoTracks();
        var overlay = Add(f, v2, 0);
        var p = TemplateProperties.Of(overlay)!.Value with { Look = HeartRateLook.Plain, Corner = OverlayCorner.TopRight, AccentHex = "#2979FF" };
        Ok(f.Service.SetClipProperties(overlay.Id, new ClipPropertyChange { Template = p }));

        Ok(f.Service.Split(MediaTime.FromFrame(100, f.Rate), new[] { overlay.Id }));
        Ok(f.Service.DuplicateClips(v2.Clips.Select(c => c.Id).Take(1).ToList()));

        Assert.Equal(3, v2.Clips.Count);
        Assert.All(v2.Clips.Cast<TemplateClip>(), c => Assert.Equal(p, TemplateProperties.Of(c)));
        f.AssertValid();
    }
}
