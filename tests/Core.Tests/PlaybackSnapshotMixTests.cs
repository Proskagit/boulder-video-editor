using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary><see cref="PlaybackSnapshot.DiffersOnlyInPresentation"/>: which timeline changes playback
/// may apply without touching any decoder (gains, mute, picture and text properties).</summary>
public class PlaybackSnapshotMixTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1" };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1" };
    private readonly VideoClip _video;
    private readonly AudioClip _music;
    private readonly MediaAsset _videoAsset;
    private long _version;

    public PlaybackSnapshotMixTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _project.Timeline.AudioTracks.Add(_a1);
        _videoAsset = Asset(MediaKind.Video);
        var musicAsset = Asset(MediaKind.Audio);
        _video = new VideoClip { MediaAssetId = _videoAsset.Id, Duration = F(50), SourceOut = F(50) };
        _music = new AudioClip { MediaAssetId = musicAsset.Id, Duration = F(80), SourceOut = F(80) };
        _v1.Clips.Add(_video);
        _a1.Clips.Add(_music);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(MediaKind kind)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4",
            Kind = kind,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(60), FrameRate = Rate, AudioCodec = "aac" }
        };
        _project.MediaAssets.Add(asset);
        return asset;
    }

    private PlaybackSnapshot Build() => PlaybackSnapshotBuilder.Build(_project, ++_version);

    private bool MixOnlyAfter(Action change)
    {
        var before = Build();
        change();
        return Build().DiffersOnlyInPresentation(before);
    }

    [Fact]
    public void Identical_timeline_is_mix_only() => Assert.True(MixOnlyAfter(() => { }));

    [Fact]
    public void Volume_of_a_video_or_audio_clip_is_mix_only()
    {
        Assert.True(MixOnlyAfter(() => _video.Volume = 0.25));
        Assert.True(MixOnlyAfter(() => _music.Volume = 2));
        Assert.True(MixOnlyAfter(() => _music.Volume = 0));
    }

    [Fact]
    public void Muting_or_unmuting_a_clip_is_mix_only()
    {
        Assert.True(MixOnlyAfter(() => _video.IsMuted = true));
        Assert.True(MixOnlyAfter(() => _video.IsMuted = false));
        Assert.True(MixOnlyAfter(() => _music.IsMuted = true));
    }

    [Fact]
    public void Picture_properties_text_and_canvas_are_presentation_only()
    {
        Assert.True(MixOnlyAfter(() => _video.Opacity = 0.5));
        Assert.True(MixOnlyAfter(() => { _video.Scale = 2; _video.RotationDegrees = 45; _video.PositionX = 10; }));
        Assert.True(MixOnlyAfter(() => _video.Crop = new CropRect(0.1, 0, 0, 0)));
        Assert.True(MixOnlyAfter(() => _videoAsset.Metadata!.Width = 640)); // source size: composition only
        Assert.True(MixOnlyAfter(() => _project.Settings.FrameHeight = 1920));

        var text = new TextClip { Text = "Title", TimelineStart = F(60), Duration = F(10) };
        _v1.Clips.Add(text);
        Assert.True(MixOnlyAfter(() => { text.Text = "Other"; text.FontSize = 90; text.Opacity = 0.3; }));
        Assert.False(MixOnlyAfter(() => text.Duration = F(20)));           // text timing is layer structure
        Assert.False(MixOnlyAfter(() => _v1.Clips.Remove(text)));
    }

    [Fact]
    public void Timing_track_or_media_changes_are_not_mix_only()
    {
        Assert.False(MixOnlyAfter(() => { _video.TimelineStart = F(1); }));
        Assert.False(MixOnlyAfter(() => _music.SourceIn = F(2)));
        Assert.False(MixOnlyAfter(() => _v1.IsHidden = true));
        Assert.False(MixOnlyAfter(() => _a1.IsMuted = true)); // a muted track drops its spans
        Assert.False(MixOnlyAfter(() => _project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V2" })));
        Assert.False(MixOnlyAfter(() => _videoAsset.IsMissing = true));
        Assert.False(MixOnlyAfter(() => _project.Settings.FrameRate = FrameRate.Fps30));
    }

    [Fact]
    public void A_gain_change_together_with_any_other_change_is_not_mix_only()
    {
        Assert.False(MixOnlyAfter(() =>
        {
            _music.Volume = 0.5;
            _video.Duration = F(40);
            _video.SourceOut = F(40);
        }));
    }
}
