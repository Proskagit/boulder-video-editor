using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Step 11.6 acceptance defect D1: the Inspector's state line of a selected media item says what the Media Browser's row
/// says — offline first, then what the analysis actually does: "Analyzing…" only while an analysis runs, "Not analysed
/// yet" for one that hasn't run (a relink without ffprobe), the error for a failed one. Not derived from the metadata.
/// </summary>
public sealed class InspectorMediaStatusTests
{
    private readonly InspectorViewModel _inspector;

    public InspectorMediaStatusTests()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        _inspector = new InspectorViewModel(new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance), new StatusService());
    }

    private static MediaAsset Asset(MediaAnalysisStatus status, bool missing = false, bool metadata = false) => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), "aive-inspector", "a.mp4"), Kind = MediaKind.Video, AnalysisStatus = status,
        IsMissing = missing, AnalysisError = status == MediaAnalysisStatus.Failed ? "FFprobe could not be found." : null,
        Metadata = metadata ? new MediaMetadata { Duration = MediaTime.FromSeconds(8), Width = 640, Height = 360 } : null
    };

    [Theory]
    [InlineData(MediaAnalysisStatus.Pending, false, false, "Not analysed yet", false, null)]
    [InlineData(MediaAnalysisStatus.Analyzing, false, false, "Analyzing…", true, null)]
    [InlineData(MediaAnalysisStatus.Completed, false, true, null, false, null)]
    [InlineData(MediaAnalysisStatus.Failed, false, false, null, false, "FFprobe could not be found.")]
    [InlineData(MediaAnalysisStatus.Pending, true, false, "Media offline", false, null)]
    [InlineData(MediaAnalysisStatus.Analyzing, true, false, "Media offline", false, null)]
    [InlineData(MediaAnalysisStatus.Completed, true, true, "Media offline", false, null)]
    [InlineData(MediaAnalysisStatus.Failed, true, false, "Media offline", false, null)]
    public void The_state_line_follows_offline_then_the_analysis(MediaAnalysisStatus status, bool missing, bool metadata,
        string? expected, bool analyzing, string? error)
    {
        var asset = Asset(status, missing, metadata);

        _inspector.ShowMedia(asset);

        Assert.Equal(expected, _inspector.AnalysisStatusText);
        Assert.Equal(expected is not null, _inspector.HasAnalysisStatusText);
        Assert.Equal(analyzing, _inspector.IsAnalyzing);
        Assert.Equal(error, _inspector.AnalysisErrorMessage);
        if (expected is not null)                                           // the same words as the Media Browser's row
            Assert.Equal(expected, new MediaBrowserItemViewModel(asset).TechnicalSummary);
    }

    [Fact]
    public void Clearing_the_selection_clears_the_state_line()
    {
        _inspector.ShowMedia(Asset(MediaAnalysisStatus.Pending));
        _inspector.ClearSelection();

        Assert.Null(_inspector.AnalysisStatusText);
        Assert.False(_inspector.IsAnalyzing);
    }
}
