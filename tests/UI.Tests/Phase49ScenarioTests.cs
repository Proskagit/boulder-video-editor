using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D068 items 6, 7, 11, 14: the first minutes of the target workflow — imported files are selected, the first clips are
/// shown whole, the save state is on the bar, a project without a training offers its import in the Inspector.</summary>
public sealed class Phase49ScenarioTests : IDisposable
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly TimelineViewModel _timeline;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-p49-" + Guid.NewGuid().ToString("N"));

    public Phase49ScenarioTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _timeline = new TimelineViewModel(_projects, _edit, new StatusService(), NullLogger<TimelineViewModel>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MediaAsset Video(string name, double seconds = 30)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-p49", name), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(seconds), FrameRate = FrameRate.Fps30, AvgFrameRate = FrameRate.Fps30, Width = 1920, Height = 1080
            }
        };
        return asset;
    }

    [Fact]
    public void Imported_files_become_the_selection_so_add_to_timeline_acts_at_once()
    {
        var browser = new MediaBrowserViewModel(_projects, null!, NullLogger<MediaBrowserViewModel>.Instance, edit: _edit);
        _projects.AddMediaAssets(new[] { Video("first.mp4") });
        browser.SelectedItems.Clear();

        var a = Video("a.mp4");
        var b = Video("b.mp4");
        _projects.AddMediaAssets(new[] { a, b });
        Assert.Empty(browser.SelectedItems);                       // files added by other means are not selected
        browser.SelectImported(new[] { a.Id, b.Id });              // what MediaImportWorkflow.Imported does

        Assert.Equal(new[] { a.Id, b.Id }, browser.SelectedItems.Select(i => i.Asset.Id));
        Assert.Equal(a.Id, browser.SelectedItem?.Asset.Id);
        Assert.True(browser.AddToTimelineCommand.CanExecute(null));
    }

    [Fact]
    public void The_first_clips_on_an_empty_timeline_are_shown_whole_and_later_edits_keep_the_zoom()
    {
        var first = Video("first.mp4", 30);
        var second = Video("second.mp4", 30);
        _projects.AddMediaAssets(new[] { first, second });
        var before = _timeline.PixelsPerSecond;

        _timeline.AddMedia(first);
        var fitted = _timeline.PixelsPerSecond;
        Assert.NotEqual(before, fitted);
        Assert.Equal(1000 / 30.0, fitted, 3);          // the fallback width (no view yet) over the 30 s of material

        _timeline.ZoomInCommand.Execute(null);
        var zoomed = _timeline.PixelsPerSecond;
        _timeline.AddMedia(second);
        Assert.Equal(zoomed, _timeline.PixelsPerSecond);
    }

    [Fact]
    public async Task The_save_state_says_not_saved_saved_at_and_unsaved_changes()
    {
        var now = new DateTime(2026, 11, 18, 10, 26, 0);
        var state = new SaveStateViewModel(_projects, now: () => now);
        Assert.Equal(Strings.SaveState_NotSaved, state.Text);

        await _projects.SaveAsAsync(_folder);
        Assert.Equal(string.Format(Strings.SaveState_SavedAt, "10:26"), state.Text);
        Assert.False(state.HasUnsavedChanges);

        Assert.True(_edit.AddTextClip(MediaTime.Zero).Success);
        Assert.Equal(Strings.SaveState_Unsaved, state.Text);
        Assert.True(state.HasUnsavedChanges);
    }

    [Fact]
    public void A_video_clip_of_a_project_without_a_training_offers_the_import()
    {
        var video = Video("climb.mp4");
        _projects.AddMediaAssets(new[] { video });
        var inspector = new InspectorViewModel(_edit, new StatusService())
        {
            Training = new TrainingSessionService(_projects)
        };
        _timeline.SelectionChanged += (_, selection) => { if (selection is not null) inspector.ShowClip(selection); };
        var import = new RelayCommand(() => { });

        _timeline.AddMedia(video);
        _timeline.OnClipPressed(_timeline.Tracks.SelectMany(t => t.Clips).Single(), toggle: false);
        Assert.False(inspector.ShowsTrainingImport);           // no command given: nothing offered

        inspector.ImportTrainingCommand = import;
        Assert.True(inspector.ShowsTrainingImport);            // inside the «FIT-тренировка» section, which is shown
        Assert.True(inspector.ShowsTraining);
    }

    /// <summary>D068 item 5: the start screen lies over the new project until the user creates one, opens one, or the new
    /// one changes.</summary>
    [Fact]
    public void The_start_screen_steps_aside_on_create_and_closes_when_the_project_changes()
    {
        var opened = 0;
        var screen = new StartScreenViewModel(null, new AsyncRelayCommand(() => { opened++; return Task.CompletedTask; }), _projects);
        Assert.True(screen.IsVisible);

        screen.OpenCommand.Execute(null);
        Assert.Equal(1, opened);
        Assert.True(screen.IsVisible);                    // the picker was cancelled: nothing opened

        screen.NewProjectCommand.Execute(null);
        Assert.False(screen.IsVisible);

        var again = new StartScreenViewModel(null, new AsyncRelayCommand(() => Task.CompletedTask), _projects);
        _projects.AddMediaAssets(new[] { Video("x.mp4") });
        Assert.False(again.IsVisible);                    // the new project changed (a file imported)
    }

    /// <summary>D068 amendment 1: three selected files, two of them on the timeline — one question for all, and all three
    /// leave the project with their clips in one Undo step.</summary>
    [Fact]
    public async Task Removing_selected_files_asks_once_and_removes_all_of_them_in_one_undo_step()
    {
        var dialogs = new ScriptedDialogs(0);
        var browser = new MediaBrowserViewModel(_projects, null!, NullLogger<MediaBrowserViewModel>.Instance,
            edit: _edit, dialogs: dialogs, status: new StatusService());
        var a = Video("a.mp4");
        var b = Video("b.mp4");
        var c = Video("c.mp4");
        _projects.AddMediaAssets(new[] { a, b, c });
        _timeline.AddMedia(a);
        _timeline.AddMedia(b);
        browser.SelectAllCommand.Execute(null);

        await browser.RemoveCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Asked);
        Assert.Empty(_projects.Current.MediaAssets);
        Assert.Empty(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips));
        _undo.Undo();
        Assert.Equal(3, _projects.Current.MediaAssets.Count);
        Assert.Equal(2, _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).Count());
    }

    /// <summary>D068 amendment 1: «Пульс» is offered only with a training; added where there is no heart rate, it says so.</summary>
    [Fact]
    public void The_heart_rate_template_needs_a_training_and_warns_where_there_is_no_heart_rate()
    {
        var status = new StatusService();
        var timeline = new TimelineViewModel(_projects, _edit, status, NullLogger<TimelineViewModel>.Instance);
        Assert.False(timeline.HasTraining);
        Assert.False(timeline.HasHeartRateAt(MediaTime.Zero));

        timeline.AddTemplateCommand.Execute("HeartRate");

        Assert.True(status.IsWarning);
        Assert.Equal(Strings.Template_NoHeartRateHere, status.Message);
    }
}
