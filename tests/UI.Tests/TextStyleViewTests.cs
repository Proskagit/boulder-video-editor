using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Phase 27 Step 27.5 (D043 §1–§3): the Inspector view binds the «Анимация» block, the placement grid (nine cells,
/// each its row and column), the three swatch rows and the outline / background controls to the view model — a control used
/// in the view edits the clip; the fades section is hidden for a text.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class TextStyleViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private sealed class FixedBox : ITextBoxMeasurer
    {
        public (double Width, double Height) Measure(TextProperties text) => (200, 50);
    }

    private static (InspectorView View, TextClip Clip, FrameSize Canvas) Setup()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        projects.Current.Settings.FrameRate = Rate;
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService(), textBoxes: new FixedBox());
        var clip = new TextClip
        {
            Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(100, Rate),
            FadeIn = MediaTime.FromFrame(10, Rate), MotionIn = new TextMotion(TextMotionKind.Slide, TextMotionSide.Left)
        };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        var canvas = new FrameSize(projects.Current.Settings.FrameWidth, projects.Current.Settings.FrameHeight);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate, Canvas: canvas));
        return (new InspectorView { DataContext = inspector }, clip, canvas);
    }

    private static T Named<T>(InspectorView view, string name) where T : Control =>
        view.GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    [Fact]
    public void The_grid_has_nine_cells_each_placing_the_text()
    {
        var (view, clip, canvas) = Setup();
        var cells = Named<UniformGrid>(view, "PlacementGrid").Children.OfType<Button>().ToList();
        Assert.Equal(9, cells.Count);
        var expected = from row in new[] { "Top", "Middle", "Bottom" } from column in new[] { "Left", "Centre", "Right" } select $"{row},{column}";
        Assert.Equal(expected, cells.Select(c => (string)c.CommandParameter!));
        Assert.All(cells, c => Assert.NotNull(ToolTip.GetTip(c)));

        var bottomRight = cells[8];
        bottomRight.Command!.Execute(bottomRight.CommandParameter);
        var position = TextPlacement.Position(canvas, TextProperties.Of(clip)!.Value, VisualProperties.Of(clip)!.Value, 200, 50,
            TextGridRow.Bottom, TextGridColumn.Right);
        Assert.Equal(position, (clip.PositionX, clip.PositionY));
    }

    [Fact]
    public void The_animation_block_shows_the_model_and_edits_it()
    {
        var (view, clip, _) = Setup();
        Assert.True(Named<StackPanel>(view, "TextAnimationSection").IsVisible);
        var appear = Named<ComboBox>(view, "AppearKind");
        Assert.Equal((int)TextAppearanceKind.Slide, appear.SelectedIndex);
        Assert.Equal(4, appear.ItemCount);

        appear.SelectedIndex = (int)TextAppearanceKind.Zoom;
        Assert.Equal(TextMotionKind.Zoom, clip.MotionIn.Kind);

        var slider = Named<Slider>(view, "AppearSlider");
        Assert.Equal((0.1, 3.0), (slider.Minimum, slider.Maximum));
        Assert.Equal(0.4, slider.Value, 9);
    }

    [Fact]
    public void The_swatch_rows_and_the_style_controls_edit_the_clip()
    {
        var (view, clip, _) = Setup();
        foreach (var (row, check) in new (string, Func<TextClip, string>)[]
                 {
                     ("TextColorSwatches", c => c.ColorHex), ("OutlineColorSwatches", c => c.Outline.ColorHex),
                     ("BackgroundColorSwatches", c => c.Background.ColorHex)
                 })
        {
            var items = Named<ItemsControl>(view, row);
            Assert.Equal(8, items.ItemCount);
            var yellow = (ColorSwatch)items.Items[2]!;
            var inspector = (InspectorViewModel)view.DataContext!;
            var command = row switch
            {
                "TextColorSwatches" => inspector.SetTextColorCommand,
                "OutlineColorSwatches" => inspector.SetOutlineColorCommand,
                _ => inspector.SetBackgroundColorCommand
            };
            command.Execute(yellow.Hex);
            Assert.Equal(yellow.Hex, check(clip));
        }

        Named<CheckBox>(view, "OutlineToggle").IsChecked = true;
        Assert.True(clip.Outline.IsEnabled);
        Named<CheckBox>(view, "BackgroundToggle").IsChecked = true;
        Assert.True(clip.Background.IsEnabled);
        Named<Slider>(view, "OutlineThicknessSlider").Value = 15;
        Assert.Equal(0.15, clip.Outline.Thickness, 9);
    }
}
