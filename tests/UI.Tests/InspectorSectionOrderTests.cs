using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.LogicalTree;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D080 §2 (the owner's order of 2026-11-24): the Inspector's sections of a clip, top to bottom.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class InspectorSectionOrderTests
{
    private static List<string> SectionIds() =>
        new InspectorView().GetLogicalDescendants().OfType<InspectorSection>().Select(s => s.SectionId ?? "").ToList();

    [Fact]
    public void A_clip_reads_transform_follow_holds_fit_colour_timing_audio_and_clip_last()
    {
        var ids = SectionIds();
        string[] order = { "frame", "template", "text", "text.animation", "people", "holds", "training", "fit", "color", "timing", "audio", "clip" };
        var at = order.Select(id => ids.IndexOf(id)).ToList();
        Assert.DoesNotContain(-1, at);
        Assert.Equal(at.OrderBy(i => i), at);
    }

    [Fact]
    public void A_group_reads_frame_holds_colour_audio()
    {
        var ids = SectionIds();
        string[] order = { "group.frame", "group.holds", "group.color", "group.audio" };
        var at = order.Select(id => ids.IndexOf(id)).ToList();
        Assert.DoesNotContain(-1, at);
        Assert.Equal(at.OrderBy(i => i), at);
    }
}
