using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.People;

/// <summary>A route area made from the FIT and the source time window of its video the attempt's events are searched in.</summary>
/// <para><see cref="FallUsable"/>: no status yet or «Попытка» — a fall can give it its lilac (amendment 3); a top's or a flash's
/// window is wanted only for the lift-off.</para>
public readonly record struct FitAreaEventWindow(Guid TemplateId, Guid ClipId, Guid AssetId, MediaTime From, MediaTime To, bool FallUsable = false);

/// <summary>
/// D078 §3: the windows of the attempt's events — per route area made from the FIT (a «Боулдер» with a FIT climb and the bounds it
/// was laid out with, anchored to a video clip), from its route start <c>T</c> − <see cref="BeforeStart"/> (the watch registers a
/// climb up to ~40 s late, research stage 0) to its laid-out end + <see cref="AfterEnd"/> (the climb's watch end + ~20 s), in the
/// clip's video's source time.
/// </summary>
public static class FitAreaEventWindows
{
    public static readonly MediaTime BeforeStart = MediaTime.FromSeconds(35);
    public static readonly MediaTime AfterEnd = MediaTime.FromSeconds(20);

    /// <summary>The intro a FIT area is laid out with before <c>T</c> (D048 §3.4: 7 s) — <c>T</c> when the area keeps none.</summary>
    private static readonly MediaTime Intro = MediaTime.FromSeconds(7);

    public static IReadOnlyList<FitAreaEventWindow> Of(Project project)
    {
        var clips = project.Timeline.VideoTracks.SelectMany(t => t.Clips).ToList();
        var videos = clips.OfType<VideoClip>().ToDictionary(c => c.Id);
        var result = new List<FitAreaEventWindow>();
        foreach (var template in clips.OfType<TemplateClip>())
        {
            // Amendment 3: the FIT's areas and a manual «Боулдер» anchored over a clip (both keep the bounds they were laid out
            // with); an area laid out by «Разложить вступление и финал» keeps none and is the user's.
            if (template.Kind != TemplateKind.Boulder || template.FitLayout is not { } layout ||
                template.AnchorClipId is not { } clipId || !videos.TryGetValue(clipId, out var clip))
                continue;
            var start = template.RouteStart ?? layout.SourceStart + Intro;
            var from = start - BeforeStart;
            result.Add(new FitAreaEventWindow(template.Id, clip.Id, clip.MediaAssetId, from < MediaTime.Zero ? MediaTime.Zero : from,
                layout.SourceEnd + AfterEnd, template.Status is BoulderStatus.Attempt or BoulderStatus.None));
        }
        return result;
    }
}
