using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D069 §7: the Inspector's «Цвет» — shown for a video clip, a slider's consecutive changes one Undo step, the
/// section's reset one step, «Автоулучшение» from the clip's frames one step.</summary>
public sealed class InspectorColorTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;
    private readonly StatusService _status = new();

    public InspectorColorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance);
    }

    /// <summary>Returns frames of one colour, records what it was asked.</summary>
    private sealed class SolidFrames(byte r, byte g, byte b) : IClipFrameSampler
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DecodedFrame>> SampleAsync(VideoClip clip, MediaAsset asset, FrameRate rate, int count, int maxWidth, CancellationToken ct)
        {
            Calls++;
            var frames = new List<DecodedFrame>();
            for (var n = 0; n < count; n++)
            {
                var px = new byte[32 * 18 * 4];
                for (var i = 0; i < 32 * 18; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (b, g, (byte)(r + i % 3), 255);
                frames.Add(new DecodedFrame(32, 18, 32 * 4, px, new SourceTimestamp(n, new TimeBase(1, 25))));
            }
            return Task.FromResult<IReadOnlyList<DecodedFrame>>(frames);
        }
    }

    private (InspectorViewModel Inspector, VideoClip Clip) Shown(IClipFrameSampler? frames = null)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-color", Guid.NewGuid().ToString("N"), "v.mp4"), Kind = MediaKind.Video,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = FrameRate.Fps25, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        var inspector = new InspectorViewModel(_edit, _status, colorFrames: frames);
        _timeline.SelectionChanged += (_, selection) => { if (selection is not null) inspector.ShowClip(selection); };
        _timeline.AddMedia(asset);
        _timeline.OnClipPressed(_timeline.Tracks.SelectMany(t => t.Clips).Single(), toggle: false);
        _undo.Clear();
        return (inspector, _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single());
    }

    [Fact]
    public void A_sliders_consecutive_changes_are_one_undo_step_and_another_slider_starts_a_new_one()
    {
        var (inspector, clip) = Shown();
        Assert.True(inspector.HasColor);
        Assert.Equal(0, inspector.ColorExposure);

        inspector.ColorExposure = 10;
        inspector.ColorExposure = 25;
        inspector.ColorExposure = 40;
        inspector.ColorTemperature = -30;

        Assert.Equal(new ColorCorrection(40, 0, 0, 0, 0, 0, -30, 0), clip.Color);
        _undo.Undo();
        Assert.Equal(40, clip.Color.Exposure);
        Assert.Equal(0, clip.Color.Temperature);
        _undo.Undo();
        Assert.True(clip.Color.IsNeutral);
        Assert.Equal(0, inspector.ColorExposure);                   // the fields follow the model
    }

    [Fact]
    public void A_fraction_is_rounded_and_the_reset_is_one_step()
    {
        var (inspector, clip) = Shown();
        inspector.ColorContrast = 12.6m;
        Assert.Equal(13, clip.Color.Contrast);
        Assert.Equal(13, inspector.ColorContrast);
        inspector.ColorSaturation = -50;

        inspector.ResetColorCommand.Execute(null);

        Assert.True(clip.Color.IsNeutral);
        _undo.Undo();
        Assert.Equal(-50, clip.Color.Saturation);
    }

    [Fact]
    public async Task Auto_enhance_measures_the_clips_frames_and_sets_the_sliders_in_one_step()
    {
        var frames = new SolidFrames(60, 75, 120);   // dark and blue
        var (inspector, clip) = Shown(frames);

        await inspector.AutoEnhanceCommand.ExecuteAsync(null);

        Assert.Equal(1, frames.Calls);
        Assert.True(clip.Color.Exposure > 0);
        Assert.True(clip.Color.Temperature > 0);
        Assert.Equal(clip.Color.Exposure, (int)inspector.ColorExposure!.Value);
        _undo.Undo();
        Assert.True(clip.Color.IsNeutral);
        Assert.False(_undo.CanUndo);
    }

    [Fact]
    public async Task Without_a_frame_source_auto_enhance_only_says_so()
    {
        var (inspector, clip) = Shown();

        await inspector.AutoEnhanceCommand.ExecuteAsync(null);

        Assert.True(_status.IsWarning);
        Assert.True(clip.Color.IsNeutral);
    }
}
