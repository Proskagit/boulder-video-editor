using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.UI.Tests;

/// <summary>D071 amendment 1 (owner, 2026-11-19): no empty space between the Inspector's sections — a group (<c>Panel.category</c>)
/// whose section is hidden takes no height, whatever was selected before.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class InspectorLayoutTests(ITestOutputHelper output)
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    [Fact]
    public void Hidden_sections_leave_no_gap_after_switching_between_kinds_of_clips()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var asset = new MediaAsset
        {
            FilePath = "C:/v.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Rate, Width = 1920, Height = 1080, AudioCodec = "aac" }
        };
        projects.AddMediaAssets(new[] { asset });
        var v1 = projects.Current.Timeline.VideoTracks[0];
        var video = new VideoClip { MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        var text = new TextClip { Text = "T", TimelineStart = MediaTime.FromFrame(50, Rate), Duration = MediaTime.FromFrame(50, Rate) };
        var template = new TemplateClip { Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromFrame(100, Rate), Duration = MediaTime.FromFrame(50, Rate) };
        v1.Clips.Add(video);
        v1.Clips.Add(text);
        v1.Clips.Add(template);
        var view = new InspectorView { DataContext = inspector };

        var selections = new Func<TimelineClipSelection>[]
        {
            () => new TimelineClipSelection(video, "v.mp4", asset, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(template, "Боулдер", null, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(text, "T", null, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(video, "v.mp4", asset, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(text, "T", null, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(template, "Боулдер", null, Rate, SelectedCount: 1),
            () => new TimelineClipSelection(video, "v.mp4", asset, Rate, SelectedCount: 1),
        };
        foreach (var select in selections)
        {
            inspector.ShowClip(select());
            Layout(view);
            AssertNoGap(view);
        }
    }

    private static void Layout(Control view)
    {
        view.Measure(new Size(420, 4000));
        view.Arrange(new Rect(0, 0, 420, 4000));
    }

    private void AssertNoGap(InspectorView view)
    {
        foreach (var panel in view.GetLogicalDescendants().OfType<Panel>().Where(p => p.Classes.Contains("category")))
        {
            var section = panel.Children.OfType<InspectorSection>().FirstOrDefault();
            if (section is null || panel.Children.Any(c => c.IsVisible)) continue;
            output.WriteLine($"{section.SectionId}: hidden, panel height {panel.Bounds.Height}, desired {panel.DesiredSize.Height}");
            Assert.True(panel.Bounds.Height == 0, $"The hidden section '{section.SectionId}' still takes {panel.Bounds.Height} px.");
        }
    }
}
