using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 10 Step 10.5: the Inspector's fade fields are bound in the real view (its XAML, reflection bindings): the two
/// NumericUpDowns show the view model's frames and write edits back; the inactive notes follow PO-8.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class FadeViewBindingTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    [Fact]
    public void The_inspector_view_binds_the_fade_fields_and_the_inactive_notes()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());

        var clip = new TextClip { Text = "Title", TimelineStart = F(0), Duration = F(50), FadeIn = F(12), FadeOut = F(7) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate, DissolveAtStart: false, DissolveAtEnd: true));

        var view = new InspectorView { DataContext = inspector };
        var fields = view.GetLogicalDescendants().OfType<NumericUpDown>()
            .Where(n => n.Maximum == 50m && n.FormatString == "0").ToList();
        Assert.Equal(2, fields.Count);                                           // Fade In, Fade Out
        Assert.Equal(new decimal?[] { 12m, 7m }, fields.Select(f => f.Value));

        var notes = view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text == "Not applied: a dissolve is on this edge.").ToList();
        Assert.Equal(new[] { false, true }, notes.Select(t => t.IsVisible));   // fade out: a dissolve on the end

        fields[0].Value = 20m;                                                   // typing in the view edits the clip
        Assert.Equal(F(20), clip.FadeIn);
    }

    [Fact]
    public void The_inspector_view_binds_the_dissolve_section()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var track = projects.Current.Timeline.VideoTracks[0];
        var a = new TextClip { Text = "A", TimelineStart = F(0), Duration = F(50) };
        var b = new TextClip { Text = "B", TimelineStart = F(50), Duration = F(50) };
        track.Clips.AddRange(new Clip[] { a, b });
        var dissolve = new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(10), LeftClipId = a.Id, RightClipId = b.Id };
        track.Transitions.Add(dissolve);
        inspector.ShowTransition(new TimelineTransitionSelection(dissolve, track, "A", "B", Rate, MaxFrames: 100));

        var view = new InspectorView { DataContext = inspector };
        var field = view.GetLogicalDescendants().OfType<NumericUpDown>().Single(n => n.Minimum == 2m);
        Assert.Equal((10m, 100m), (field.Value, field.Maximum));
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Longest that fits here: 100 frames");
        var remove = view.GetLogicalDescendants().OfType<Button>().Single(x => Equals(x.Content, "Remove Dissolve"));
        Assert.Same(inspector.RemoveDissolveCommand, remove.Command);

        field.Value = 20m;                                                       // typing in the view edits the dissolve
        Assert.Equal(F(20), dissolve.Duration);
    }

    /// <summary>17 (4x on "bars", the real control): a speed the edit service refuses must not stay in the field. The
    /// control ignores the change raised while it is still sending its value; the field shows the clip speed again once
    /// the Inspector announces the fields (right after the rejection, and when the field loses the focus).</summary>
    [Fact]
    public void A_refused_speed_does_not_stay_in_the_real_field()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var status = new StatusService();
        var inspector = new InspectorViewModel(edit, status);
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-speed-field", Guid.NewGuid().ToString("N"), "v.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 }
        };
        projects.AddMediaAssets(new[] { asset });
        Assert.True(edit.AddClip(asset.Id).Success);
        Assert.True(edit.Split(F(25)).Success);
        var track = projects.Current.Timeline.VideoTracks[0];
        var (a, b) = (track.Clips[0], track.Clips[1]);
        Assert.True(edit.AddTransition(a.Id, b.Id, F(24)).Success);
        inspector.ShowClip(new TimelineClipSelection(b, "B", asset, Rate, DissolveAtStart: true));

        var view = new InspectorView { DataContext = inspector };
        var speed = view.GetLogicalDescendants().OfType<NumericUpDown>().Single(n => n.Maximum == 4m);

        speed.Value = 4m;                                                        // refused: not enough media before B
        Assert.Contains("not enough media", status.Message);
        Assert.Equal(ClipSpeed.Normal, ((MediaBackedClip)b).Speed);

        inspector.ShowModelValues();                                             // what losing the focus does
        Assert.Equal(1m, speed.Value);
    }
}
