using AiVideoEditor.Core.Common;
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

/// <summary>Phase 26 (D041 §2): «Заполнить кадр» / «Вписать в кадр» in the real Inspector view and the clip's context menu
/// on the timeline — present for a video or image clip, bound to the commands, absent for text.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class FramingViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static (InspectorViewModel Inspector, ProjectService Projects, TimelineEditService Edit) Shell()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        return (new InspectorViewModel(edit, new StatusService()), projects, edit);
    }

    private static List<Button> FramingButtons(InspectorView view) =>
        view.GetLogicalDescendants().OfType<WrapPanel>().Single(p => p.Name == "FramingButtons").Children.OfType<Button>().ToList();

    [Fact]
    public void The_Inspector_shows_both_buttons_for_a_video_clip_bound_to_the_commands()
    {
        var (inspector, projects, _) = Shell();
        var asset = new MediaAsset { FilePath = "C:/v.mp4", Kind = MediaKind.Video };
        projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "v.mp4", asset, Rate));

        var view = new InspectorView { DataContext = inspector };
        var panel = view.GetLogicalDescendants().OfType<WrapPanel>().Single(p => p.Name == "FramingButtons");
        var buttons = FramingButtons(view);

        Assert.True(panel.IsVisible);
        Assert.Equal(new[] { Strings.Frame_Fill, Strings.Frame_Fit }, buttons.Select(b => (string)b.Content!));
        Assert.Same(inspector.FillFrameCommand, buttons[0].Command);
        Assert.Same(inspector.FitFrameCommand, buttons[1].Command);
        Assert.Equal(new[] { Strings.Frame_FillTip, Strings.Frame_FitTip }, buttons.Select(b => (string)ToolTip.GetTip(b)!));
    }

    [Fact]
    public void The_Inspector_hides_the_buttons_for_a_text_clip()
    {
        var (inspector, projects, _) = Shell();
        var clip = new TextClip { Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate));

        var view = new InspectorView { DataContext = inspector };
        Assert.False(view.GetLogicalDescendants().OfType<WrapPanel>().Single(p => p.Name == "FramingButtons").IsVisible);
    }

    [Fact]
    public void The_context_menu_has_the_two_commands_bound_to_the_clicked_clip()
    {
        var (_, projects, edit) = Shell();
        var timeline = new TimelineViewModel(projects, edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
        Assert.True(edit.AddTextClip(MediaTime.Zero).Success);
        var vm = timeline.Tracks.SelectMany(t => t.Clips).Single();

        var menu = TimelineView.ClipMenu(timeline, vm);
        var items = menu.ItemsSource!.Cast<MenuItem>().ToList();

        Assert.Equal(new[] { Strings.Frame_Fill, Strings.Frame_Fit }, items.Select(i => (string)i.Header!));
        Assert.Same(timeline.FillFrameCommand, items[0].Command);
        Assert.Same(timeline.FitFrameCommand, items[1].Command);
        Assert.All(items, i => Assert.Same(vm, i.CommandParameter));
        Assert.All(items, i => Assert.False(i.Command!.CanExecute(i.CommandParameter)));   // a text clip: unavailable
    }
}
