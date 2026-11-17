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

/// <summary>D063 in the real Inspector view: the keyframe section of a video clip (its owner text, the add / remove button
/// and previous / next bound to the commands, the easing only on a keyframe), absent for a text; «Боулдер»'s plate corner.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class KeyframeViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static (InspectorViewModel Inspector, ProjectService Projects) Shell()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        return (new InspectorViewModel(edit, new StatusService()) { Playhead = () => MediaTime.FromFrame(10, Rate) }, projects);
    }

    private static T Named<T>(InspectorView view, string name) where T : Control =>
        view.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void A_video_clip_shows_the_keyframe_section_bound_to_its_commands()
    {
        var (inspector, projects) = Shell();
        var asset = new MediaAsset { FilePath = "C:/v.mp4", Kind = MediaKind.Video };
        projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate), SourceOut = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "v.mp4", asset, Rate));

        var view = new InspectorView { DataContext = inspector };
        Assert.True(Named<StackPanel>(view, "KeyframeSection").IsVisible);
        var toggle = Named<Button>(view, "KeyframeToggleButton");
        Assert.Same(inspector.ToggleKeyframeHereCommand, toggle.Command);
        Assert.Equal(Strings.Keyframe_Add, toggle.Content);
        Assert.Equal(string.Format(Strings.Keyframe_OwnerClip, 0), Named<TextBlock>(view, "KeyframeOwnerText").Text);
        Assert.False(Named<ComboBox>(view, "KeyframeEasingBox").IsEffectivelyVisible);

        toggle.Command!.Execute(null);
        Assert.Single(clip.Keyframes!.Keys);
        inspector.ShowModelValues();                                            // the shell's refresh after a timeline change
        Assert.Equal(Strings.Keyframe_Remove, toggle.Content);
        Assert.True(Named<ComboBox>(view, "KeyframeEasingBox").IsEffectivelyVisible);
        Assert.Equal(4, Named<ComboBox>(view, "KeyframeEasingBox").ItemCount);
    }

    [Fact]
    public void A_text_clip_has_no_keyframe_section()
    {
        var (inspector, projects) = Shell();
        var clip = new TextClip { Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate));
        var view = new InspectorView { DataContext = inspector };
        Assert.False(Named<StackPanel>(view, "KeyframeSection").IsVisible);
    }

    [Fact]
    public void A_route_area_offers_its_plate_in_the_four_corners()
    {
        var (inspector, projects) = Shell();
        var plate = new TemplateClip { Kind = TemplateKind.Boulder, Route = "7", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(plate);
        inspector.ShowClip(new TimelineClipSelection(plate, "Боулдер", null, Rate));
        var view = new InspectorView { DataContext = inspector };
        var corners = Named<ItemsControl>(view, "PlateCornersButtons");
        Assert.Equal(4, corners.ItemCount);
        Assert.True(inspector.PlateCorners.Single(o => o.Value == OverlayCorner.BottomLeft).IsSelected);

        inspector.SetPlateCornerCommand.Execute(OverlayCorner.TopRight);
        Assert.Equal(OverlayCorner.TopRight, plate.PlateCorner);
        inspector.ShowModelValues();
        Assert.True(inspector.PlateCorners.Single(o => o.Value == OverlayCorner.TopRight).IsSelected);
    }
}
