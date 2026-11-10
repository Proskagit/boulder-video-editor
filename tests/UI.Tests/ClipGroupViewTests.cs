using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D058 §3, §5 in the real Inspector view: the group's panel instead of the single clip's sections, its fields bound
/// (a change in the view reaches every clip), «—» where the clips differ; the draggable labels find their fields.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class ClipGroupViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    [Fact]
    public void The_group_panel_replaces_the_clip_sections_and_its_fields_are_bound()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var asset = new MediaAsset { FilePath = "C:/v.mp4", Kind = MediaKind.Video };
        projects.AddMediaAssets(new[] { asset });
        var track = projects.Current.Timeline.VideoTracks[0];
        var a = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        var b = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.FromFrame(50, Rate), Duration = MediaTime.FromFrame(50, Rate), Opacity = 0.5 };
        track.Clips.Add(a);
        track.Clips.Add(b);
        var view = new InspectorView { DataContext = inspector };
        T Named<T>(string name) where T : Control => view.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

        inspector.ShowClip(new TimelineClipSelection(a, "v.mp4", asset, Rate, SelectedCount: 1));
        Assert.False(Named<StackPanel>("GroupPanel").IsVisible);
        Assert.True(inspector.IsSingleClipSelected);
        // D058 §5: the draggable labels are bound to their fields.
        Assert.All(view.GetLogicalDescendants().OfType<ScrubLabel>(), l => Assert.NotNull(l.Target));

        inspector.ShowClip(new TimelineClipSelection(b, "v.mp4", asset, Rate, SelectedCount: 2) { Selected = new Clip[] { a, b } });
        Assert.True(Named<StackPanel>("GroupPanel").IsVisible);
        Assert.False(inspector.IsSingleClipSelected);
        var background = Named<ComboBox>("GroupBackgroundBox");
        Assert.Equal(2, background.ItemCount);
        Assert.Null(Named<CommitNumericUpDown>("GroupOpacityBox").Value);   // 100 % and 50 %: «—»

        background.SelectedIndex = 1;                                         // as the user picks «Размытый»
        Assert.Equal(ClipBackground.Blur, a.Background);
        Assert.Equal(ClipBackground.Blur, b.Background);
        undo.Undo();
        Assert.Equal(ClipBackground.Black, a.Background);
        Assert.Equal(ClipBackground.Black, b.Background);
    }
}
