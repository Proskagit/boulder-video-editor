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

/// <summary>
/// D078 amendment 4 (the owner): a group selected in the Media Browser's real list stays a group — the list's
/// <c>SelectedItem</c> binding resets a multiple selection when the view model sets it, so removing through the menu on a
/// file of the group, or a rubber band, must never set it inside the group.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class MediaBrowserSelectionViewTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static (MediaBrowserViewModel Browser, ListBox List, ScriptedDialogs Dialogs, ProjectService Projects, TimelineEditService Edit) Setup()
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var status = new StatusService();
        var analysis = new MediaAnalysisCoordinator(new MediaRemovalUiTests.NoAnalysis(), projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new MediaRemovalUiTests.NoImport(), projects, analysis, status, NullLogger<MediaImportWorkflow>.Instance);
        var dialogs = new ScriptedDialogs(0);
        var browser = new MediaBrowserViewModel(projects, import, NullLogger<MediaBrowserViewModel>.Instance, edit: edit, dialogs: dialogs, status: status);
        for (var i = 0; i < 3; i++)
            projects.AddMediaAssets(new[]
            {
                new MediaAsset
                {
                    FilePath = Path.Combine(Path.GetTempPath(), "aive-sel", Guid.NewGuid().ToString("N"), $"v{i}.mp4"), Kind = MediaKind.Video,
                    AnalysisStatus = MediaAnalysisStatus.Completed,
                    Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), Width = 64, Height = 36, FrameRate = Rate }
                }
            });
        foreach (var item in browser.Items) Assert.True(edit.AddClip(item.Asset.Id).Success);
        var view = new MediaBrowserView { DataContext = browser };
        var list = view.GetLogicalDescendants().OfType<ListBox>().Single(l => l.Name == "MediaList");
        return (browser, list, dialogs, projects, edit);
    }

    [Fact]
    public async Task The_menus_removal_on_a_file_of_the_group_removes_the_whole_group()
    {
        var (browser, list, dialogs, projects, _) = Setup();
        list.SelectedItems!.Add(browser.Items[0]);
        list.SelectedItems.Add(browser.Items[1]);
        list.SelectedItems.Add(browser.Items[2]);
        Assert.Equal(3, browser.SelectedItems.Count);

        browser.PrepareContextMenu(browser.Items[2]);          // the menu on a file of the group, not the first one
        await browser.RemoveContextItemCommand.ExecuteAsync(null);

        var asked = Assert.Single(dialogs.Asked);
        Assert.Equal(string.Format(Strings.Media_RemoveQuestionFiles, 3, 3), asked.Message);
        Assert.Empty(projects.Current.MediaAssets);
    }

    [Fact]
    public void A_rubber_band_keeps_every_touched_file_selected_in_the_list()
    {
        var (browser, list, _, _, _) = Setup();
        browser.SelectByMarquee(browser.Items.ToList(), Array.Empty<MediaBrowserItemViewModel>());
        Assert.Equal(3, list.SelectedItems!.Count);
        Assert.Equal(3, browser.SelectedItems.Count);
    }

    [Fact]
    public void Removing_the_files_also_removes_the_video_tracks_they_leave_empty()
    {
        var (browser, list, _, projects, edit) = Setup();
        // A «Боулдер» over the first clip on a track of its own, then every file removed.
        Assert.True(edit.AddTemplateClip(TemplateKind.Boulder, MediaTime.FromSeconds(1)).Success);
        Assert.Equal(2, projects.Current.Timeline.VideoTracks.Count);
        Assert.True(edit.RemoveMediaMany(browser.Items.Select(i => i.Asset.Id).ToList()).Success);
        Assert.Single(projects.Current.Timeline.VideoTracks);   // the main track stays, the emptied one goes
    }
}
