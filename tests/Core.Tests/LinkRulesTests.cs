using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D032 R1: a link joins exactly one video clip and one audio clip; images are never linked.</summary>
public class LinkRulesTests
{
    private static readonly Guid Asset = Guid.NewGuid();

    private static VideoClip Video(Guid? link = null) => new() { MediaAssetId = Asset, LinkId = link };
    private static AudioClip Audio(Guid? link = null) => new() { MediaAssetId = Asset, LinkId = link };

    [Fact]
    public void Unlinked_clips_and_a_video_audio_pair_are_valid()
    {
        var link = Guid.NewGuid();
        Assert.Null(LinkRules.Validate(new Clip[] { Video(), Audio(), new TextClip() }));
        Assert.Null(LinkRules.Validate(new Clip[] { Video(link), Audio(link), Video(), Audio() }));
    }

    public static TheoryData<string, Clip[]> Invalid()
    {
        var link = Guid.NewGuid();
        return new TheoryData<string, Clip[]>
        {
            { "one clip", new Clip[] { Video(link) } },
            { "two video clips", new Clip[] { Video(link), Video(link) } },
            { "two audio clips", new Clip[] { Audio(link), Audio(link) } },
            { "three clips", new Clip[] { Video(link), Audio(link), Audio(link) } },
        };
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_link_of_anything_but_one_video_and_one_audio_clip_is_invalid(string what, Clip[] clips)
    {
        Assert.Equal("A link must join exactly one video clip and one audio clip.", LinkRules.Validate(clips));
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void An_image_is_never_linked()
    {
        var link = Guid.NewGuid();
        Assert.Equal("Only video and audio clips can be linked.",
            LinkRules.Validate(new Clip[] { new ImageClip { MediaAssetId = Asset, LinkId = link }, Audio(link) }));
    }

    [Fact]
    public void The_partner_is_the_other_clip_of_the_link()
    {
        var link = Guid.NewGuid();
        var (video, audio, other) = (Video(link), Audio(link), Audio());
        var all = new Clip[] { video, audio, other };
        Assert.Same(audio, LinkRules.PartnerOf(video, all));
        Assert.Same(video, LinkRules.PartnerOf(audio, all));
        Assert.Null(LinkRules.PartnerOf(other, all));
    }

    [Fact]
    public void A_sequence_is_checked_across_its_tracks()
    {
        var sequence = new Sequence();
        var link = Guid.NewGuid();
        var v1 = new Track { Type = TrackType.Video };
        var a1 = new Track { Type = TrackType.Audio };
        v1.Clips.Add(Video(link));
        sequence.VideoTracks.Add(v1);
        sequence.AudioTracks.Add(a1);
        Assert.NotNull(LinkRules.Validate(sequence));

        a1.Clips.Add(Audio(link));
        Assert.Null(LinkRules.Validate(sequence));
    }
}
