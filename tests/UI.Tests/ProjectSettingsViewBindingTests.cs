using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>Phase 13 Step 13.6: the real views — "Project Settings…" between Import Media and Export with the current
/// settings as its tooltip, following the editing lock; the dialog's controls bound to the draft.</summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class ProjectSettingsViewBindingTests
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();

    public ProjectSettingsViewBindingTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
    }

    private sealed class NoDialog : IProjectSettingsDialog
    {
        public Task ShowAsync(ProjectSettingsViewModel settings) => Task.CompletedTask;
    }

    [Fact]
    public void The_button_sits_between_import_and_export_shows_the_settings_and_follows_the_lock()
    {
        var workflow = new ProjectSettingsWorkflow(_projects, _edit, _status, _lock, new NoDialog());
        var toolbar = new ToolbarViewModel(_undo, null!, null!, _status, null, _lock, null, workflow);
        var view = new ToolbarView { DataContext = toolbar };

        var row = view.GetLogicalDescendants().OfType<StackPanel>().First(p => p.Orientation == Avalonia.Layout.Orientation.Horizontal);
        var buttons = row.Children.OfType<Button>().Select(b => b.Content as string).ToList();
        var settings = row.Children.OfType<Button>().Single(b => Equals(b.Content, "Project Settings…"));
        Assert.Equal(buttons.IndexOf("Import Media") + 1, buttons.IndexOf("Project Settings…"));
        Assert.Equal(buttons.IndexOf("Project Settings…") + 1, buttons.IndexOf("Export"));
        var index = row.Children.IndexOf(settings);
        Assert.IsType<Avalonia.Controls.Shapes.Rectangle>(row.Children[index - 1]);      // separators on both sides
        Assert.IsType<Avalonia.Controls.Shapes.Rectangle>(row.Children[index + 1]);
        Assert.Equal("Project Settings — 1920 × 1080 · 30 FPS (provisional)", ToolTip.GetTip(settings));

        Assert.True(_edit.SetProjectSettings(1080, 1920, FrameRate.Fps25).Success);
        Assert.Equal("Project Settings — 1080 × 1920 · 25 FPS", ToolTip.GetTip(settings));
        _undo.Undo();
        Assert.Equal("Project Settings — 1920 × 1080 · 30 FPS (provisional)", ToolTip.GetTip(settings));

        Assert.Same(toolbar.ProjectSettingsCommand, settings.Command);
        Assert.True(settings.Command!.CanExecute(null));
        using (_lock.Acquire())
            Assert.False(settings.Command.CanExecute(null));
        Assert.True(settings.Command.CanExecute(null));
    }

    [Fact]
    public void The_dialog_view_binds_the_draft()
    {
        var draft = new ProjectSettingsViewModel(_projects.Current.Settings, _edit, _status, () => _lock.IsLocked);
        var view = new ProjectSettingsView { DataContext = draft };
        Control Named(string name) => view.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == name);

        var preset = (ComboBox)Named("PresetBox");
        var rate = (ComboBox)Named("RateBox");
        var width = (CommitNumericUpDown)Named("WidthBox");
        var height = (CommitNumericUpDown)Named("HeightBox");
        Assert.Same(draft.SelectedPreset, preset.SelectedItem);
        Assert.Equal(11, preset.ItemCount);
        Assert.Same(draft.SelectedRate, rate.SelectedItem);
        Assert.Equal(9, rate.ItemCount);
        Assert.Equal((1920m, 1080m), (width.Value, height.Value));
        Assert.Equal(2m, width.Increment);

        preset.SelectedItem = ProjectSettingsViewModel.AllPresets.Single(p => p.Width == 1080 && p.Height == 1350);
        Assert.Equal((1080m, 1350m), (draft.Width, draft.Height));
        Assert.Equal((1080m, 1350m), (width.Value, height.Value));
        var apply = (Button)Named("ApplyButton");
        Assert.Same(draft.ApplyCommand, apply.Command);
        Assert.True(apply.Command!.CanExecute(null));

        draft.Width = 1081;
        Assert.False(apply.Command.CanExecute(null));
        Assert.True(((Button)Named("CancelButton")).IsCancel);
        Assert.Equal((1920, 1080), (_projects.Current.Settings.FrameWidth, _projects.Current.Settings.FrameHeight));

        // Export settings (Step 13.9): three combo boxes on the draft, the current values selected.
        var quality = (ComboBox)Named("QualityBox");
        var speed = (ComboBox)Named("SpeedBox");
        var bitrate = (ComboBox)Named("BitrateBox");
        Assert.Equal((4, 3, 5), (quality.ItemCount, speed.ItemCount, bitrate.ItemCount));
        Assert.Same(draft.SelectedQuality, quality.SelectedItem);
        Assert.Same(draft.SelectedSpeed, speed.SelectedItem);
        Assert.Same(draft.SelectedBitrate, bitrate.SelectedItem);
        quality.SelectedItem = draft.Qualities.Single(o => o.Value == ExportQuality.Compact);
        bitrate.SelectedItem = draft.Bitrates.Single(o => o.Value == 320);
        Assert.Equal((ExportQuality.Compact, 320), (draft.DraftExport.Quality, draft.DraftExport.AudioBitrateKbps));
        Assert.Same(ExportEncoding.Default, _projects.Current.Settings.Export);   // the view writes the draft only
    }
}
