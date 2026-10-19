using AiVideoEditor.Core.Localization;
namespace AiVideoEditor.Core.Entities;

/// <summary>
/// The structural rule of links (D032 R1): the clips sharing a <see cref="MediaBackedClip.LinkId"/> are exactly one
/// <see cref="VideoClip"/> (on a video track) and one <see cref="AudioClip"/> (on an audio track). Images never carry a
/// link.
/// </summary>
public static class LinkRules
{
    /// <summary>Checks every link of <paramref name="clips"/> (all clips of a sequence, or of a planned result); returns a
    /// short user-facing reason for the first violation, or null.</summary>
    public static string? Validate(IEnumerable<Clip> clips) =>
        Validate(clips.Select(c => (c, (c as MediaBackedClip)?.LinkId)));

    /// <summary>As <see cref="Validate(IEnumerable{Clip})"/> with each clip's link as an edit would leave it.</summary>
    public static string? Validate(IEnumerable<(Clip Clip, Guid? LinkId)> clips)
    {
        var groups = new Dictionary<Guid, (int Video, int Audio)>();
        foreach (var (clip, link) in clips)
        {
            if (link is not { } linkId) continue;
            if (clip is not (VideoClip or AudioClip)) return Messages.Link_OnlyMedia;
            var (video, audio) = groups.GetValueOrDefault(linkId);
            groups[linkId] = clip is VideoClip ? (video + 1, audio) : (video, audio + 1);
        }

        foreach (var (video, audio) in groups.Values)
        {
            if (video != 1 || audio != 1) return Messages.Link_OneVideoOneAudio;
        }
        return null;
    }

    /// <summary><see cref="Validate(IEnumerable{Clip})"/> for every clip of <paramref name="sequence"/>.</summary>
    public static string? Validate(Sequence sequence) =>
        Validate(sequence.VideoTracks.Concat(sequence.AudioTracks).SelectMany(t => t.Clips));

    /// <summary>The other clip of <paramref name="clip"/>'s link among <paramref name="clips"/>, or null.</summary>
    public static MediaBackedClip? PartnerOf(Clip clip, IEnumerable<Clip> clips) =>
        clip is MediaBackedClip { LinkId: { } linkId }
            ? clips.OfType<MediaBackedClip>().FirstOrDefault(c => c.LinkId == linkId && c != clip)
            : null;
}
