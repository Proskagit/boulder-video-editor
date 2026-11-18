using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D069 amendment 2 (the owner's check): two clips on one canvas each keep their own «Боулдер» — a plate the edit
/// carries onto another plate's place goes to a free track above its clip (or a new one, in the same step) instead of pushing
/// the other plate.</summary>
public class AnchoredPlateTrackTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);
    private static (long, long) Span(Clip c) => (c.TimelineStart.ToNearestFrame(Rate), c.TimelineEnd.ToNearestFrame(Rate));
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_clip_moved_with_its_plate_over_another_plate_keeps_both_plates(bool magnet)
    {
        var f = new TimelineFixture();
        f.Settings.FrameRate = Rate;
        f.Settings.IsFrameRateLocked = true;
        var a = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "a.mp4"), F(0).Ticks, F(100).Ticks);
        var b = f.Place<VideoClip>(f.V1, f.Video(60, Rate, "b.mp4"), F(100).Ticks, F(200).Ticks);
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V2: the plates
        Ok(f.Service.AddTrack(TrackType.Video));                                     // V3: where B goes
        var v2 = f.Project.Timeline.VideoTracks[1];
        var v3 = f.Project.Timeline.VideoTracks[2];
        var plateA = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(10), Duration = F(50), AnchorClipId = a.Id };
        var plateB = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = F(110), Duration = F(50), AnchorClipId = b.Id };
        v2.Clips.Add(plateA);
        v2.Clips.Add(plateB);
        f.Project.Timeline.MagneticMainTrack = magnet;
        var before = f.Snapshot();
        var tracks = f.Project.Timeline.VideoTracks.Count;

        Ok(f.Service.MoveClips(new[] { b.Id }, -100, v3.Id));                       // B over A on the next track, from 0

        Assert.Equal((0L, 100L), Span(b));
        Assert.Equal((10L, 60L), Span(plateA));                                       // A's plate stays where it was
        Assert.Contains(plateA, v2.Clips);
        Assert.Equal((10L, 60L), Span(plateB));                                       // B's plate with B …
        var plateTrack = f.Project.Timeline.VideoTracks.Single(t => t.Clips.Contains(plateB));
        Assert.True(plateTrack.Order > v3.Order);                                     // … above B, on a track of its own
        Assert.Equal(tracks + 1, f.Project.Timeline.VideoTracks.Count);
        f.AssertValid();

        f.UndoRedo.Undo();                                                            // one step: the new track goes too
        Assert.Equal(before, f.Snapshot());
        Assert.Equal(tracks, f.Project.Timeline.VideoTracks.Count);
    }
}
