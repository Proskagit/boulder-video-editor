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

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>
/// D039 §8 / D040 §4: the sliders in the real Inspector view (Opacity, Volume, Fade In / Out, the hold line width) — each
/// shows its field's value and limits, a drag (several values in a row) writes the clip and is one undo step (D017's merge
/// of consecutive changes of one field), the field follows the slider and typing moves it.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class SliderViewBindingTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    [Fact]
    public void The_opacity_slider_follows_the_field_and_a_drag_is_one_undo_step()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var inspector = new InspectorViewModel(edit, new StatusService());
        var clip = new TextClip { Text = "Title", TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(50, Rate) };
        projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        inspector.ShowClip(new TimelineClipSelection(clip, "Title", null, Rate, DissolveAtStart: false, DissolveAtEnd: false));

        var view = new InspectorView { DataContext = inspector };
        var slider = view.GetLogicalDescendants().OfType<Slider>().Single(s => s.Name == "OpacitySlider");
        Assert.Equal(100, slider.Value);
        Assert.Equal(0, slider.Minimum);
        Assert.Equal(100, slider.Maximum);
        Assert.False(slider.Focusable);                                          // Space / arrows stay the editor's keys

        foreach (var value in new[] { 90.0, 75.0, 60.0 }) slider.Value = value;   // a drag
        Assert.Equal(0.6, VisualProperties.Of(clip)!.Value.Opacity, 6);
        Assert.Equal(60m, inspector.OpacityPercent);

        undo.Undo();                                                             // the whole drag at once
        Assert.Equal(1.0, VisualProperties.Of(clip)!.Value.Opacity, 6);
        Assert.False(undo.CanUndo);

        inspector.OpacityPercent = 35m;                                          // typing moves the slider
        Assert.Equal(35, slider.Value);
    }

    private sealed class Rig
    {
        public readonly UndoRedoService Undo = new();
        public readonly ProjectService Projects;
        public readonly TimelineEditService Edit;
        public readonly InspectorViewModel Inspector;
        public readonly InspectorView View;

        public Rig()
        {
            Projects = new ProjectService(Undo, NullLogger<ProjectService>.Instance);
            Edit = new TimelineEditService(Projects, Undo, NullLogger<TimelineEditService>.Instance);
            Inspector = new InspectorViewModel(Edit, new StatusService());
            View = new InspectorView { DataContext = Inspector };
        }

        public MediaAsset Asset(MediaKind kind)
        {
            var asset = new MediaAsset
            {
                FilePath = Path.Combine(Path.GetTempPath(), "aive-ui-tests", Guid.NewGuid().ToString("N"), kind == MediaKind.Audio ? "a.wav" : "v.mp4"),
                Kind = kind, AnalysisStatus = MediaAnalysisStatus.Completed,
                Metadata = new MediaMetadata
                {
                    Duration = MediaTime.FromFrame(250, Rate), FrameRate = kind == MediaKind.Audio ? null : Rate,
                    AvgFrameRate = kind == MediaKind.Audio ? null : Rate,
                    Width = kind == MediaKind.Audio ? null : 64, Height = kind == MediaKind.Audio ? null : 36, AudioCodec = "aac"
                }
            };
            Projects.AddMediaAssets(new[] { asset });
            return asset;
        }

        public Clip Add(MediaKind kind)
        {
            var result = Edit.AddClip(Asset(kind).Id);
            Assert.True(result.Success);
            var clip = Projects.Current.Timeline.VideoTracks.Concat(Projects.Current.Timeline.AudioTracks)
                .SelectMany(t => t.Clips).Single(c => c.Id == result.ClipIds.First());
            Show(clip);
            return clip;
        }

        public void Show(Clip clip) =>
            Inspector.ShowClip(new TimelineClipSelection(clip, "clip", null, ProjectRate, DissolveAtStart: false, DissolveAtEnd: false));

        /// <summary>The project's rate: 25 fps after a video clip, the provisional default with sound only.</summary>
        public FrameRate ProjectRate => Projects.Current.Settings.FrameRate;

        public Slider Slider(string name) => View.GetLogicalDescendants().OfType<Slider>().Single(s => s.Name == name);
    }

    [Theory]
    [InlineData(MediaKind.Video)]
    [InlineData(MediaKind.Audio)]
    public void The_fade_sliders_follow_their_fields_and_a_drag_is_one_undo_step(MediaKind kind)
    {
        var rig = new Rig();
        var clip = rig.Add(kind);                                    // 10 s
        var fadeIn = rig.Slider("FadeInSlider");
        var fadeOut = rig.Slider("FadeOutSlider");
        Assert.Equal(0, fadeIn.Value);
        Assert.Equal(0, fadeIn.Minimum);
        var length = (double)MediaTime.FromSeconds(10).ToFrameFloor(rig.ProjectRate);
        Assert.Equal(length, fadeIn.Maximum);                        // the field's maximum: the clip's length
        Assert.Equal(length, fadeOut.Maximum);
        Assert.Equal(rig.Inspector.MaxFadeOutFrames, (decimal)fadeOut.Maximum);
        Assert.False(fadeIn.Focusable);
        var before = rig.Undo.CurrentPosition;                       // after the clip was added

        foreach (var value in new[] { 5.0, 12.0, 20.0 }) fadeIn.Value = value;   // a drag
        Assert.Equal(MediaTime.FromFrame(20, rig.ProjectRate), clip.FadeIn);
        Assert.Equal(20m, rig.Inspector.FadeInFrames);
        foreach (var value in new[] { 3.0, 9.0 }) fadeOut.Value = value;
        Assert.Equal(MediaTime.FromFrame(9, rig.ProjectRate), clip.FadeOut);

        rig.Undo.Undo();                                             // the Fade Out drag at once
        Assert.Equal(MediaTime.Zero, clip.FadeOut);
        rig.Undo.Undo();                                             // the Fade In drag at once
        Assert.Equal(MediaTime.Zero, clip.FadeIn);
        Assert.Same(before, rig.Undo.CurrentPosition);
        rig.Show(clip);

        rig.Inspector.FadeInFrames = 7m;                             // typing moves the slider
        Assert.Equal(7, fadeIn.Value);
    }

    [Fact]
    public void The_fade_maximum_follows_the_clip_length()
    {
        var rig = new Rig();
        var clip = rig.Add(MediaKind.Video);
        Assert.True(rig.Edit.TrimClip(clip.Id, ClipEdge.End, MediaTime.FromFrame(100, rig.ProjectRate)).Success);
        rig.Show(clip);                                              // the timeline shows the trimmed clip again

        Assert.Equal(100, rig.Slider("FadeInSlider").Maximum);
        Assert.Equal(100, rig.Slider("FadeOutSlider").Maximum);
        Assert.Equal(rig.Inspector.MaxFadeInFrames, (decimal)rig.Slider("FadeInSlider").Maximum);
    }

    [Fact]
    public void The_volume_slider_follows_the_field_and_a_drag_is_one_undo_step()
    {
        var rig = new Rig();
        var clip = (AudioClip)rig.Add(MediaKind.Audio);
        var before = rig.Undo.CurrentPosition;                       // after the clip was added
        var slider = rig.Slider("VolumeSlider");
        Assert.Equal(100, slider.Value);
        Assert.Equal(0, slider.Minimum);
        Assert.Equal(200, slider.Maximum);
        Assert.False(slider.Focusable);

        foreach (var value in new[] { 120.0, 150.0, 180.0 }) slider.Value = value;
        Assert.Equal(1.8, clip.Volume, 6);
        Assert.Equal(180m, rig.Inspector.VolumePercent);

        rig.Undo.Undo();
        Assert.Equal(1.0, clip.Volume, 6);
        Assert.Same(before, rig.Undo.CurrentPosition);
        rig.Show(clip);

        rig.Inspector.VolumePercent = 40m;
        Assert.Equal(40, slider.Value);
    }

    [Fact]
    public void The_hold_line_width_slider_follows_the_field_and_a_drag_is_one_undo_step()
    {
        var rig = new Rig();
        var markup = new HoldMarkupViewModel(rig.Edit, rig.Projects, new StatusService());
        rig.Inspector.HoldMarkup = markup;
        var clip = (VideoClip)rig.Add(MediaKind.Video);
        var before = rig.Undo.CurrentPosition;
        markup.SetClip(clip);
        var slider = rig.Slider("StrokeWidthSlider");
        Assert.Equal(HoldMarkup.DefaultStrokeWidth1080, slider.Value);
        Assert.Equal(HoldMarkupRules.MinStrokeWidth1080, slider.Minimum);
        Assert.Equal(HoldMarkupRules.MaxStrokeWidth1080, slider.Maximum);
        Assert.False(slider.Focusable);

        foreach (var value in new[] { 6.0, 9.0, 12.0 }) slider.Value = value;
        Assert.Equal(12, clip.HoldMarkup!.StrokeWidth1080);
        Assert.Equal(12m, markup.StrokeWidth);

        rig.Undo.Undo();
        Assert.Equal(HoldMarkup.DefaultStrokeWidth1080, clip.HoldMarkup?.StrokeWidth1080 ?? HoldMarkup.DefaultStrokeWidth1080);
        Assert.Same(before, rig.Undo.CurrentPosition);

        markup.StrokeWidth = 3m;
        Assert.Equal(3, slider.Value);
    }
}
