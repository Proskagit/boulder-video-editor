using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>Phase 17 Step 17.3 (D032 R1, R9): the model rules the audio edits build on — the D025 §4 handles for audio
/// clips, the sequence validator's link rule.</summary>
public class AudioModelTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static MediaAsset Asset(MediaKind kind) => new()
    {
        FilePath = @"C:\media\a." + (kind == MediaKind.Audio ? "wav" : "mp4"), Kind = kind,
        Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), AudioCodec = "aac" }
    };

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void An_audio_clip_has_the_handles_of_a_video_clip_with_the_same_timing(long numerator, long denominator)
    {
        Assert.True(ClipSpeed.TryFromRatio(numerator, denominator, out var speed));
        var state = new ClipState(F(40), F(50), F(30), F(30) + SpeedTiming.SourceLength(50, speed, Rate), speed);
        var video = Asset(MediaKind.Video);
        var audio = Asset(MediaKind.Audio);

        Assert.Equal(DissolveHandles.After(new VideoClip { MediaAssetId = video.Id }, state, video, Rate),
            DissolveHandles.After(new AudioClip { MediaAssetId = audio.Id }, state, audio, Rate));
        Assert.Equal(DissolveHandles.Before(new VideoClip { MediaAssetId = video.Id }, state, video, Rate),
            DissolveHandles.Before(new AudioClip { MediaAssetId = audio.Id }, state, audio, Rate));
        Assert.True(DissolveHandles.After(new AudioClip { MediaAssetId = audio.Id }, state, audio, Rate) < DissolveHandles.Unlimited);
    }

    [Fact]
    public void An_audio_clip_of_unknown_duration_has_no_handles()
    {
        var unknown = new MediaAsset { FilePath = @"C:\media\b.wav", Kind = MediaKind.Audio };
        var state = new ClipState(F(40), F(50), F(30), F(80));
        Assert.Equal(0, DissolveHandles.After(new AudioClip { MediaAssetId = unknown.Id }, state, unknown, Rate));
        Assert.Equal(0, DissolveHandles.Before(new AudioClip { MediaAssetId = unknown.Id }, state, unknown, Rate));
    }

    [Fact]
    public void The_sequence_validator_checks_links()
    {
        var fixture = new TimelineFixture();
        var video = fixture.Video(10, Rate);
        var v = fixture.Place<VideoClip>(fixture.V1, video, 0, F(25).Ticks);
        var a = fixture.Place<AudioClip>(fixture.A1, video, 0, F(25).Ticks);
        var find = (Guid id) => fixture.Project.MediaAssets.FirstOrDefault(x => x.Id == id);
        Assert.Null(TimelineValidator.ValidateSequence(fixture.Project.Timeline, fixture.Rate, find));

        v.LinkId = Guid.NewGuid();
        Assert.Equal("A link must join exactly one video clip and one audio clip.",
            TimelineValidator.ValidateSequence(fixture.Project.Timeline, fixture.Rate, find));

        a.LinkId = v.LinkId;
        Assert.Null(TimelineValidator.ValidateSequence(fixture.Project.Timeline, fixture.Rate, find));
    }
}
