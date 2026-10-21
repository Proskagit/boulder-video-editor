using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Phase 29 (D045 §6): the Inspector view binds a template's section to the view model — only the kind's fields
/// are visible, a field typed in the view edits the clip, the three status buttons and the nine route colours are there and
/// edit it; the text, transform and fades sections stay hidden.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class TemplateViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static (InspectorView View, TemplateClip Clip) Setup(TemplateKind kind, BoulderStatus status = BoulderStatus.None)
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        projects.Current.Settings.FrameRate = Rate;
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var clip = new TemplateClip
        {
            Kind = kind, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(100, Rate),
            Title = TemplateRules.Defaults(kind).Title, AccentHex = TemplateRules.DefaultAccentHex, Status = status,
            StatusAt = MediaTime.FromFrame(50, Rate)
        };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        var canvas = new FrameSize(projects.Current.Settings.FrameWidth, projects.Current.Settings.FrameHeight);
        inspector.ShowClip(new TimelineClipSelection(clip, "T", null, Rate, Canvas: canvas));
        return (new InspectorView { DataContext = inspector }, clip);
    }

    private static T Named<T>(InspectorView view, string name) where T : Control =>
        view.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static bool Shown(Control control) => control.GetLogicalAncestors().OfType<Control>().Prepend(control).All(c => c.IsVisible);

    [Fact]
    public void Boulder_shows_its_fields_and_edits_the_clip_from_the_view()
    {
        var (view, clip) = Setup(TemplateKind.Boulder);

        Assert.True(Named<StackPanel>(view, "TemplateSection").IsVisible);
        Assert.True(Shown(Named<TextBox>(view, "TemplateRouteField")));
        Assert.False(Shown(Named<TextBox>(view, "TemplateDateField")));
        Assert.False(Shown(Named<TextBox>(view, "TemplateTitleField")));
        Assert.False(Named<StackPanel>(view, "TextAnimationSection").IsVisible);

        Named<TextBox>(view, "TemplateRouteField").Text = "7";
        Named<TextBox>(view, "TemplateGradeField").Text = "7A";
        Assert.Equal(("7", "7A"), (clip.Route, clip.Grade));

        var statuses = Named<ItemsControl>(view, "TemplateStatusButtons");
        Assert.Equal(3, statuses.ItemCount);
        var inspector = (InspectorViewModel)view.DataContext!;
        Assert.False(Shown(Named<Button>(view, "TemplateStatusAtButton")));    // D045 §17: no status, no moment
        inspector.SetTemplateStatusCommand.Execute(((TemplateStatusOption)statuses.Items[1]!).Status);
        Assert.Equal(BoulderStatus.Top, clip.Status);

        var swatches = Named<ItemsControl>(view, "TemplateAccentSwatches");
        Assert.Equal(9, swatches.ItemCount);
        inspector.SetTemplateAccentCommand.Execute(((ColorSwatch)swatches.Items[5]!).Hex);
        Assert.Equal("#2979FF", clip.AccentHex);
    }

    [Fact]
    public void Boulder_with_a_status_shows_at_playhead_and_its_moment()
    {
        var (view, _) = Setup(TemplateKind.Boulder, BoulderStatus.Attempt);
        var inspector = (InspectorViewModel)view.DataContext!;

        var atPlayhead = Named<Button>(view, "TemplateStatusAtButton");
        Assert.True(Shown(atPlayhead));
        Assert.Same(inspector.SetTemplateStatusAtPlayheadCommand, atPlayhead.Command);
        Assert.Equal(string.Format(Strings.TemplateStatusAt_Value, 2.0), Named<TextBlock>(view, "TemplateStatusAtText").Text);
    }

    [Theory]
    [InlineData(TemplateKind.Rec, "TemplateDateField")]
    [InlineData(TemplateKind.Vhs, "TemplateDateField")]
    [InlineData(TemplateKind.Highlight, "TemplateSubtitleField")]
    public void The_other_kinds_show_their_own_field_and_edit_it(TemplateKind kind, string field)
    {
        var (view, clip) = Setup(kind);
        var box = Named<TextBox>(view, field);
        Assert.True(Shown(box));
        Assert.False(Shown(Named<TextBox>(view, "TemplateRouteField")));

        box.Text = "27.09.2026";
        Assert.Equal("27.09.2026", kind == TemplateKind.Highlight ? clip.Subtitle : clip.Date);
    }
}
