using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests.Playback;

/// <summary>
/// D057 §3.14: Left / Right step by the source frames of the active video clip — 30 fps → 1/30 s, 60 → 1/60, 240 → 1/240, a
/// 240 fps clip at 0.25× → 1/60 s; never finer than the highest source rate; one project frame where no video clip is; the
/// topmost visible clip is the active one.
/// </summary>
public class FrameNavigationTests
{
    private static readonly FrameRate Project = FrameRate.Fps30;
    private readonly Sequence _timeline = new();
    private readonly List<MediaAsset> _assets = new();

    public FrameNavigationTests()
    {
        _timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        _timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V2" });
    }

    private MediaAsset? Asset(Guid id) => _assets.FirstOrDefault(a => a.Id == id);

    private VideoClip Clip(int track, double startSeconds, double seconds, FrameRate rate, ClipSpeed? speed = null)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\m\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = rate, AvgFrameRate = rate }
        };
        _assets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.FromSeconds(startSeconds), Duration = MediaTime.FromSeconds(seconds),
            SourceIn = MediaTime.Zero, SourceOut = MediaTime.FromSeconds(seconds), Speed = speed ?? ClipSpeed.Normal
        };
        _timeline.VideoTracks[track].Clips.Add(clip);
        return clip;
    }

    private MediaTime Step(MediaTime at, int direction) => FrameNavigation.Step(_timeline, Asset, at, direction, Project);

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(240)]
    public void A_step_is_one_frame_of_the_active_clips_source(int fps)
    {
        var rate = new FrameRate(fps, 1);
        Clip(0, 0, 10, rate);
        Clip(1, 20, 5, new FrameRate(240, 1));                             // makes the grid 240 everywhere
        var at = MediaTime.FromSeconds(1);
        var next = Step(at, 1);
        Assert.Equal(MediaTime.FromSeconds(1) + MediaTime.FromFrame(1, rate), next);
        Assert.Equal(at, Step(next, -1));
    }

    [Fact]
    public void A_slow_motion_clip_steps_by_its_source_frames_on_the_timeline()
    {
        Clip(0, 0, 10, new FrameRate(240, 1), ClipSpeed.FromSteps(5));
        var next = Step(MediaTime.FromSeconds(1), 1);
        Assert.Equal(MediaTime.FromSeconds(1) + MediaTime.FromFrame(1, new FrameRate(60, 1)), next);   // 1/240 s of source = 1/60 s
    }

    [Fact]
    public void Where_no_video_clip_is_a_step_is_one_project_frame()
    {
        Clip(0, 0, 1, new FrameRate(240, 1));
        Assert.Equal(MediaTime.FromSeconds(5) + MediaTime.FromFrame(1, Project), Step(MediaTime.FromSeconds(5), 1));
        Assert.Equal(MediaTime.FromSeconds(5) - MediaTime.FromFrame(1, Project), Step(MediaTime.FromSeconds(5), -1));
        Assert.Equal(MediaTime.Zero, Step(MediaTime.Zero, -1));
    }

    [Fact]
    public void The_topmost_visible_clip_is_the_active_one()
    {
        Clip(0, 0, 10, FrameRate.Fps30);
        Clip(1, 0, 10, new FrameRate(120, 1));
        Assert.Equal(MediaTime.FromSeconds(1) + MediaTime.FromFrame(1, new FrameRate(120, 1)), Step(MediaTime.FromSeconds(1), 1));
        _timeline.VideoTracks[1].IsHidden = true;
        Assert.Equal(MediaTime.FromSeconds(1) + MediaTime.FromFrame(1, FrameRate.Fps30), Step(MediaTime.FromSeconds(1), 1));
    }

    [Fact]
    public void A_step_ends_at_the_clips_end_and_never_goes_finer_than_the_grid()
    {
        var clip = Clip(0, 0, 1, new FrameRate(240, 1));
        var last = clip.TimelineEnd - MediaTime.FromFrame(1, new FrameRate(240, 1));
        Assert.Equal(clip.TimelineEnd, Step(last, 1));
        Assert.Equal(new FrameRate(240, 1), FrameNavigation.NavigationRate(_timeline, Asset, Project));
    }

    [Fact]
    public void An_ntsc_clip_steps_onto_the_grid_holding_its_next_frame()
    {
        Clip(0, 0, 10, FrameRate.Ntsc30);
        Clip(1, 20, 5, new FrameRate(240, 1));
        var at = MediaTime.FromFrame(30, FrameRate.Ntsc30);               // a source frame start of the 29.97 clip
        var next = Step(at, 1);
        var grid = new FrameRate(240, 1);
        Assert.Equal(next, MediaTime.FromFrame(next.ToFrameFloor(grid), grid));                    // on the 240 grid
        var clip = (VideoClip)_timeline.VideoTracks[0].Clips[0];
        Assert.Equal(31, FrameNavigation.Shown(clip, next.ToFrameFloor(grid), grid, FrameRate.Ntsc30));          // what the Preview shows
        Assert.Equal(30, FrameNavigation.Shown(clip, next.ToFrameFloor(grid) - 1, grid, FrameRate.Ntsc30));      // the first such frame
    }
}
