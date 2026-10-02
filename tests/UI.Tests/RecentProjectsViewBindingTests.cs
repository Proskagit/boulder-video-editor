using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D026 §6, Step 11.8: the real toolbar view puts <c>Recent ▾</c> right after Open, shows it only with a
/// drop-down view model, follows the editing lock, and keeps long names and folders on one trimmed line.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class RecentProjectsViewBindingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (ToolbarViewModel Toolbar, RecentProjectsViewModel Recent, EditingLock Lock) Toolbar(bool withRecent = true)
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var status = new StatusService();
        var editingLock = new EditingLock();
        var store = new RecentProjectsStore(Path.Combine(_root, "recent-projects.json"), NullLogger<RecentProjectsStore>.Instance);
        var analysis = new MediaAnalysisCoordinator(new NullAnalysis(), projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var workflow = new ProjectFileWorkflow(projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(),
            status, NullLogger<ProjectFileWorkflow>.Instance, recentProjects: store);
        var recent = new RecentProjectsViewModel(store, workflow, status, editingLock);
        var toolbar = new ToolbarViewModel(undo, workflow, null!, status, null, editingLock, withRecent ? recent : null);
        return (toolbar, recent, editingLock);
    }

    private sealed class NullAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Success(new MediaMetadata()));
    }

    [Fact]
    public void Recent_sits_right_after_open_and_follows_the_editing_lock()
    {
        var (toolbar, _, editingLock) = Toolbar();
        var view = new ToolbarView { DataContext = toolbar };

        var row = view.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var open = row.Children.OfType<Button>().Single(b => Equals(b.Content, "Open"));
        var recent = Assert.IsType<DropDownButton>(row.Children[row.Children.IndexOf(open) + 1]);
        Assert.Equal("Recent", recent.Content);
        Assert.True(recent.IsVisible);
        Assert.True(recent.IsEnabled);
        Assert.Same(toolbar.Recent, recent.DataContext);
        Assert.IsType<Flyout>(recent.Flyout);

        using (editingLock.Acquire())
            Assert.False(recent.IsEnabled);
        Assert.True(recent.IsEnabled);
    }

    [Fact]
    public void Without_a_drop_down_view_model_the_button_is_hidden()
    {
        var (toolbar, _, _) = Toolbar(withRecent: false);
        var view = new ToolbarView { DataContext = toolbar };

        Assert.False(view.GetLogicalDescendants().OfType<DropDownButton>().Single().IsVisible);
    }

    [Fact]
    public void An_entry_shows_its_name_folder_and_state_on_trimmed_lines_with_open_and_remove()
    {
        var (toolbar, recent, _) = Toolbar();
        var view = new ToolbarView { DataContext = toolbar };
        var flyout = (Flyout)view.GetLogicalDescendants().OfType<DropDownButton>().Single().Flyout!;
        var content = Assert.IsType<StackPanel>(flyout.Content);
        Assert.Equal(400, content.Width);                                           // a fixed width: long text can't widen it
        var list = content.Children.OfType<ItemsControl>().Single();

        var item = new RecentProjectItemViewModel(
            new RecentProject(@"C:\" + new string('p', 200) + @"\Film", new string('N', 200), DateTimeOffset.UtcNow), recent);
        var row = list.ItemTemplate!.Build(item)!;
        row.DataContext = item;
        var texts = row.GetLogicalDescendants().OfType<TextBlock>().ToList();
        var buttons = row.GetLogicalDescendants().OfType<Button>().ToList();

        Assert.Equal(TextTrimming.CharacterEllipsis, texts.Single(t => t.Text == item.Name).TextTrimming);
        Assert.Equal(TextTrimming.PrefixCharacterEllipsis, texts.Single(t => t.Text == item.FolderPath).TextTrimming);
        Assert.Contains(texts, t => t.Text == "Checking…");
        Assert.Same(item.OpenCommand, buttons[0].Command);
        Assert.Same(item.RemoveCommand, buttons[1].Command);
        Assert.Equal("✕", buttons[1].Content);
        Assert.Equal(item.ToolTip, ToolTip.GetTip(buttons[0]));

        var empty = content.Children.OfType<TextBlock>().Single(t => t.Text!.StartsWith("No recent projects"));
        empty.DataContext = recent;
        Assert.False(empty.IsVisible);                                              // not read yet
    }
}
