using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.3: the clip's context menu — the clicked clip's operations, each the command of its button or key;
/// Split only with the playhead inside; the hold markup and the FIT's command only on a video clip.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class ClipContextMenuTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;

    public ClipContextMenuTests()
    {
        var undo = new UndoRedoService();
        _projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        _projects.Current.Settings.FrameRate = Rate;
        _edit = new TimelineEditService(_projects, undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    private static List<string?> Names(ContextMenu menu) => menu.ItemsSource!.OfType<MenuItem>().Select(i => i.Name).ToList();

    private static MenuItem Item(ContextMenu menu, string name) => menu.ItemsSource!.OfType<MenuItem>().Single(i => i.Name == name);

    [Fact]
    public void A_text_clip_has_the_editing_items_and_split_only_with_the_playhead_inside()
    {
        Assert.True(_edit.AddTextClip(MediaTime.Zero).Success);
        var clip = _timeline.Tracks.SelectMany(t => t.Clips).Single();
        _timeline.OnClipPressed(clip, toggle: false);

        _timeline.SetPlayhead(clip.Clip.TimelineEnd + MediaTime.FromSeconds(1));
        var outside = TimelineView.ClipMenu(_timeline, clip);
        Assert.DoesNotContain("SplitItem", Names(outside));
        Assert.Equal(new[] { "CopyItem", "PasteItem", "TrimStartItem", "TrimEndItem", "FillFrameItem", "FitFrameItem", "DeleteItem" },
            Names(outside).Where(n => n is not null));
        Assert.DoesNotContain("MarkHoldsItem", Names(outside));            // a text clip: no hold markup

        _timeline.SetPlayhead(MediaTime.FromFrame(10, Rate));
        var inside = TimelineView.ClipMenu(_timeline, clip);
        Assert.Same(_timeline.SplitAtPlayheadCommand, Item(inside, "SplitItem").Command);
        Assert.Same(_timeline.CopyCommand, Item(inside, "CopyItem").Command);
        Assert.Same(_timeline.PasteCommand, Item(inside, "PasteItem").Command);
        Assert.Same(_timeline.TrimStartToPlayheadCommand, Item(inside, "TrimStartItem").Command);
        Assert.Same(_timeline.DeleteSelectedCommand, Item(inside, "DeleteItem").Command);
        Assert.Equal(Strings.Menu_SplitAtPlayhead, Item(inside, "SplitItem").Header);

        Item(inside, "SplitItem").Command!.Execute(null);                  // runs what the key runs
        Assert.Equal(2, _projects.Current.Timeline.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public void A_video_clip_offers_the_hold_markup_and_the_fit_only_when_available()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-menu", "v.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        var clip = _timeline.Tracks.SelectMany(t => t.Clips).Single();
        var mark = new RelayCommand(() => { });
        var fitAvailable = false;
        var fit = new RelayCommand(() => { }, () => fitAvailable);
        _timeline.MarkHoldsCommand = mark;
        _timeline.RecognizeFitClimbsCommand = fit;

        var menu = TimelineView.ClipMenu(_timeline, clip);
        Assert.Same(mark, Item(menu, "MarkHoldsItem").Command);
        Assert.DoesNotContain("RecognizeFitItem", Names(menu));            // no training

        fitAvailable = true;
        Assert.Same(fit, Item(TimelineView.ClipMenu(_timeline, clip), "RecognizeFitItem").Command);
    }
}
