using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Controls;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using AiVideoEditor.UI.Views.Panels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 28 Step 28.4 (D044 §5–§8): the transition library's interface through the real shell wiring — "Transition"
/// opening the menu of the five types on a video track and adding the crossfade on an audio track; each type added with
/// its default or the longest that fits (the status says which), never another type; the Inspector's TRANSITION section —
/// the type buttons (one that cannot fit disabled with the reason), a push's direction, the duration in seconds of whole
/// frames with the 0.1 s arrow step, its refusals; a type change keeping or clamping the duration (the status says so),
/// Undo / Redo, Delete, a locked track, a running export; the audio crossfade keeping its frames field; the zone's glyph;
/// the views' bindings and the menu's five items.
/// </summary>
[Collection(AvaloniaControlsCollection.Name)]
public sealed class TransitionLibraryUiTests : IAsyncLifetime
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TransitionLibraryUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance);
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status, editingLock: _lock),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance, _lock),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private TimelineViewModel Timeline => _vm.Timeline;
    private InspectorViewModel Inspector => _vm.Inspector;
    private Track V1 => _projects.Current.Timeline.VideoTracks[0];
    private FrameRate Rate => _projects.Current.Settings.FrameRate;
    private MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);
    private static string N(decimal d) => d.ToString("0.##", CultureInfo.CurrentCulture);

    private MediaAsset Asset(double seconds, FrameRate rate, MediaKind kind = MediaKind.Video)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-transition-ui", Guid.NewGuid().ToString("N"), kind == MediaKind.Audio ? "a.mp3" : "v.mp4"),
            Kind = kind,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = kind == MediaKind.Audio
                ? new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), AudioCodec = "mp3", AudioSampleRate = 48000 }
                : new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    /// <summary>A <paramref name="seconds"/> s video split at <paramref name="at"/>: two clips meeting with handles.</summary>
    private (Clip A, Clip B) SplitVideo(double seconds = 20, long at = 100, FrameRate? rate = null)
    {
        Assert.True(_edit.AddClip(Asset(seconds, rate ?? FrameRate.Fps25).Id).Success);
        Assert.True(_edit.Split(F(at)).Success);
        return (V1.Clips[0], V1.Clips[1]);
    }

    /// <summary>Two whole videos appended: no source handles on either side of the cut.</summary>
    private (Clip A, Clip B) WholeVideos()
    {
        var asset = Asset(4, FrameRate.Fps25);
        Assert.True(_edit.AddClip(asset.Id).Success);
        Assert.True(_edit.AddClip(asset.Id).Success);
        return (V1.Clips[0], V1.Clips[1]);
    }

    private TimelineClipViewModel ClipVm(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.Id);
    private TimelineTransitionViewModel ZoneVm() => Timeline.Tracks.SelectMany(t => t.Transitions).Single();

    private void SelectBoth(Clip a, Clip b)
    {
        Timeline.ClearSelection();
        Timeline.OnClipPressed(ClipVm(a), toggle: false);
        Timeline.OnClipPressed(ClipVm(b), toggle: true);
    }

    private Transition AddSelected(Clip a, Clip b, string type)
    {
        SelectBoth(a, b);
        Timeline.AddTransitionCommand.Execute(type);
        return Assert.Single(V1.Transitions);
    }

    private TransitionTypeOption Option(string type) => Inspector.TransitionTypes.Single(o => o.TypeId == type);

    // --- "Transition" -----------------------------------------------------------------------------------------------

    [Fact]
    public void Transition_opens_the_menu_on_a_video_track_and_adds_the_crossfade_on_an_audio_track()
    {
        var (a, b) = SplitVideo();
        var menus = 0;
        Timeline.TransitionMenuRequested += (_, _) => menus++;
        Assert.False(Timeline.OpenTransitionMenuCommand.CanExecute(null));
        SelectBoth(a, b);
        Assert.True(Timeline.OpenTransitionMenuCommand.CanExecute(null));

        Timeline.OpenTransitionMenuCommand.Execute(null);
        Assert.Equal(1, menus);
        Assert.Empty(V1.Transitions);                                                    // the menu chooses the type

        var music = Asset(20, FrameRate.Fps25, MediaKind.Audio);
        var a1 = _projects.Current.Timeline.AudioTracks[0];
        Assert.True(_edit.AddClip(music.Id, a1.Id, F(0)).Success);
        Assert.True(_edit.Split(F(100), new[] { a1.Clips[0].Id }).Success);
        SelectBoth(a1.Clips[0], a1.Clips[1]);
        Timeline.OpenTransitionMenuCommand.Execute(null);
        Assert.Equal(1, menus);                                                          // no menu on an audio track
        Assert.Equal(TransitionRules.Crossfade, Assert.Single(a1.Transitions).TransitionTypeId);

        using (_lock.Acquire())
        {
            SelectBoth(a, b);
            Assert.False(Timeline.OpenTransitionMenuCommand.CanExecute(null));
            Assert.False(Timeline.AddTransitionCommand.CanExecute(TransitionRules.Zoom));
        }
    }

    [Theory]
    [InlineData(TransitionRules.CrossDissolve, 25, "Dissolve")]
    [InlineData(TransitionRules.DipToBlack, 15, "Dip to black")]
    [InlineData(TransitionRules.Flash, 5, "Flash")]
    [InlineData(TransitionRules.Zoom, 8, "Zoom")]
    [InlineData(TransitionRules.Push, 8, "Push")]
    public void Each_type_is_added_with_its_default_selected_and_shown(string type, long frames, string name)
    {
        var (a, b) = SplitVideo();
        var t = AddSelected(a, b, type);

        Assert.Equal((type, F(frames)), (t.TransitionTypeId, t.Duration));
        Assert.Equal(string.Format("{0} added: {1}.", name, TransitionNames.Duration(frames, Rate)), _status.Message);
        Assert.True(ZoneVm().IsSelected);
        Assert.Equal(TransitionNames.Glyph(type), ZoneVm().Glyph);
        Assert.True(Inspector.IsVideoTransitionSelected);
        Assert.False(Inspector.IsAudioTransitionSelected);
        Assert.Equal("TRANSITION", Inspector.TransitionHeader);
        Assert.True(Option(type).IsSelected);
        Assert.Equal(type == TransitionRules.Push, Inspector.IsPushSelected);
        if (type == TransitionRules.Push)
            Assert.True(Inspector.TransitionDirections.Single(d => d.Direction == TransitionDirection.FromRight).IsSelected);
        Assert.Equal(TransitionNames.SecondsOf(frames, Rate), Inspector.TransitionSeconds);
        Assert.Equal(TransitionNames.Duration(frames, Rate), Inspector.TransitionDurationDisplay);
    }

    [Fact]
    public void Where_the_default_does_not_fit_the_longest_is_added_and_said()
    {
        var (a, b) = SplitVideo(seconds: 4, at: 95);                                     // B has 5 frames
        var t = AddSelected(a, b, TransitionRules.DipToBlack);

        Assert.Equal(F(10), t.Duration);                                                  // ⌈F/2⌉ ≤ 5 → 10, not 15
        Assert.Equal($"Dip to black added: {TransitionNames.Duration(10, Rate)}, the longest that fits here.", _status.Message);
    }

    [Fact]
    public void A_type_needing_handles_is_refused_without_them_and_its_button_says_why()
    {
        var (a, b) = WholeVideos();
        SelectBoth(a, b);
        Timeline.AddTransitionCommand.Execute(TransitionRules.Push);
        Assert.Equal(Messages.Transition_NotEnoughMediaType, _status.Message);
        Assert.Empty(V1.Transitions);

        Timeline.AddTransitionCommand.Execute(TransitionRules.Zoom);                     // needs none
        Assert.Single(V1.Transitions);
        foreach (var type in new[] { TransitionRules.CrossDissolve, TransitionRules.Push })
            Assert.Equal((false, Messages.Transition_NotEnoughMediaType), (Option(type).IsAvailable, Option(type).Tip));
        foreach (var type in new[] { TransitionRules.DipToBlack, TransitionRules.Flash, TransitionRules.Zoom })
            Assert.Equal((true, (string?)null), (Option(type).IsAvailable, Option(type).Tip));
    }

    // --- the Inspector ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_type_change_keeps_or_clamps_the_duration_says_so_and_undoes_in_one_step()
    {
        var (a, b) = SplitVideo();
        var t = AddSelected(a, b, TransitionRules.CrossDissolve);                       // 25 frames

        Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Flash);               // 1 s → 0.48 s (12 frames at 25)
        Assert.Equal((TransitionRules.Flash, F(12)), (t.TransitionTypeId, t.Duration));
        Assert.Equal($"Transition changed to Flash; its duration is now {TransitionNames.Duration(12, Rate)}.", _status.Message);
        Assert.Equal(TransitionNames.Glyph(TransitionRules.Flash), ZoneVm().Glyph);
        Assert.True(Option(TransitionRules.Flash).IsSelected);

        Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Zoom);                // 12 frames lie in Zoom's 5–25
        Assert.Equal(F(12), t.Duration);
        Assert.Equal("Transition changed to Zoom.", _status.Message);

        _undo.Undo();
        Assert.Equal((TransitionRules.Flash, F(12)), (t.TransitionTypeId, t.Duration));
        Assert.True(Option(TransitionRules.Flash).IsSelected);                           // the section follows undo
        _undo.Undo();
        Assert.Equal((TransitionRules.CrossDissolve, F(25)), (t.TransitionTypeId, t.Duration));
        _undo.Redo();
        Assert.Equal(TransitionRules.Flash, t.TransitionTypeId);
    }

    [Fact]
    public void A_push_shows_and_changes_its_direction()
    {
        var (a, b) = SplitVideo();
        var t = AddSelected(a, b, TransitionRules.Push);
        Assert.True(Inspector.IsPushSelected);

        Inspector.SetTransitionDirectionCommand.Execute(TransitionDirection.FromTop);
        Assert.Equal(TransitionDirection.FromTop, t.Direction);
        Assert.True(Inspector.TransitionDirections.Single(d => d.Direction == TransitionDirection.FromTop).IsSelected);
        Assert.Equal("Change Push Direction", (_undo.CurrentPosition as IUndoableCommand)?.Description);

        Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Zoom);
        Assert.False(Inspector.IsPushSelected);
        _undo.Undo();
        _undo.Undo();
        Assert.Equal(TransitionDirection.FromRight, t.Direction);
    }

    [Fact]
    public void The_seconds_field_stores_whole_frames_and_the_arrows_step_on_the_tenth_of_a_second_grid()
    {
        _projects.Current.Settings.FrameRate = FrameRate.Fps24;
        var (a, b) = SplitVideo(rate: FrameRate.Fps24);
        var t = AddSelected(a, b, TransitionRules.Zoom);                                 // 0.3 s → 7 frames at 24 fps
        Assert.Equal(F(7), t.Duration);
        Assert.Equal(0.29m, Inspector.TransitionSeconds);
        Assert.Equal($"{N(0.29m)} s · 7 frames", Inspector.TransitionDurationDisplay);    // no promise of 0.3 s

        Inspector.StepTransitionSecondsCommand.Execute(1);                               // the up arrow from 0.29
        Assert.Equal(F(10), t.Duration);                                                  // 0.3 s is 7 frames too: 0.4 s → 10 (0.42)
        Assert.Equal(0.42m, Inspector.TransitionSeconds);
        Inspector.StepTransitionSecondsCommand.Execute(-1);                              // the down arrow: 0.4 s is 10 too → 0.3 s → 7
        Assert.Equal(F(7), t.Duration);

        Inspector.TransitionSeconds = 0.39m;                                              // typed 0.1 s above: the nearest frame, no step
        Assert.Equal(F(9), t.Duration);
        Inspector.TransitionSeconds = 0.75m;                                              // typed: the nearest frame (18)
        Assert.Equal(F(18), t.Duration);
        Assert.Equal("Change Dissolve Duration", (_undo.CurrentPosition as IUndoableCommand)?.Description);
        _undo.Undo();                                                                     // the duration changes merged
        Assert.Equal(F(7), t.Duration);

        Inspector.TransitionSeconds = 1.5m;                                               // above Zoom's 1 s
        Assert.Equal(string.Format(Messages.Transition_DurationRange, 5, 24), _status.Message);
        Assert.Equal(F(7), t.Duration);
        Assert.Equal(0.29m, Inspector.TransitionSeconds);                                 // the field shows the model again
    }

    [Fact]
    public void A_locked_track_and_a_running_export_change_nothing()
    {
        var (a, b) = SplitVideo();
        var t = AddSelected(a, b, TransitionRules.Push);
        var before = (t.TransitionTypeId, t.Duration, t.Direction);

        V1.IsLocked = true;
        Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Zoom);
        Assert.Contains("locked", _status.Message);
        Inspector.SetTransitionDirectionCommand.Execute(TransitionDirection.FromLeft);
        Assert.Contains("locked", _status.Message);
        Inspector.TransitionSeconds = 0.5m;
        Assert.Contains("locked", _status.Message);
        V1.IsLocked = false;

        using (_lock.Acquire())
        {
            Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Zoom);
            Inspector.SetTransitionDirectionCommand.Execute(TransitionDirection.FromLeft);
            Inspector.TransitionSeconds = 0.5m;
            Assert.False(Timeline.DeleteSelectedCommand.CanExecute(null));
        }
        Assert.Equal(before, (t.TransitionTypeId, t.Duration, t.Direction));
    }

    [Fact]
    public void Delete_removes_a_library_transition_in_one_step()
    {
        var (a, b) = SplitVideo();
        AddSelected(a, b, TransitionRules.Flash);

        Timeline.DeleteSelectedCommand.Execute(null);

        Assert.Empty(V1.Transitions);
        Assert.Equal("Transition removed", _status.Message);
        _undo.Undo();
        Assert.Equal(TransitionRules.Flash, Assert.Single(V1.Transitions).TransitionTypeId);
    }

    [Fact]
    public void The_audio_crossfade_keeps_its_frames_field_and_has_no_type_buttons()
    {
        var music = Asset(20, FrameRate.Fps25, MediaKind.Audio);
        var a1 = _projects.Current.Timeline.AudioTracks[0];
        Assert.True(_edit.AddClip(music.Id, a1.Id, F(0)).Success);
        Assert.True(_edit.Split(F(100), new[] { a1.Clips[0].Id }).Success);
        SelectBoth(a1.Clips[0], a1.Clips[1]);
        Timeline.AddDissolveCommand.Execute(null);

        Assert.True(Inspector.IsAudioTransitionSelected);
        Assert.False(Inspector.IsVideoTransitionSelected);
        Assert.Equal("CROSSFADE", Inspector.TransitionHeader);
        Inspector.DissolveFrames = 30;
        Assert.Equal(F(30), Assert.Single(a1.Transitions).Duration);
    }

    // --- the views ----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_inspector_section_binds_the_types_the_direction_and_the_seconds()
    {
        var (a, b) = WholeVideos();
        AddSelected(a, b, TransitionRules.Zoom);
        var view = new InspectorView { DataContext = Inspector };

        var section = view.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "TransitionLibrarySection");
        var types = view.GetLogicalDescendants().OfType<ItemsControl>().Single(i => i.Name == "TransitionTypeButtons");
        Assert.Same(Inspector.TransitionTypes, types.ItemsSource);
        Assert.Equal(new[] { "Dissolve", "Dip to black", "Flash", "Zoom", "Push" }, Inspector.TransitionTypes.Select(o => o.Name));
        var directions = view.GetLogicalDescendants().OfType<ItemsControl>().Single(i => i.Name == "TransitionDirectionButtons");
        Assert.Same(Inspector.TransitionDirections, directions.ItemsSource);
        var field = view.GetLogicalDescendants().OfType<CommitNumericUpDown>().Single(n => n.Name == "TransitionSecondsField");
        Assert.Equal(Inspector.TransitionSeconds, field.Value);
        Assert.True(section.IsVisible);
    }

    [Fact]
    public void The_timeline_menu_lists_the_five_types_with_their_ids()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", "TimelineView.axaml"));
        System.Xml.Linq.XNamespace ui = "https://github.com/avaloniaui";

        var items = xaml.Descendants(ui + "MenuFlyout").Single().Elements(ui + "MenuItem").ToList();

        Assert.Equal(TransitionRules.VideoTypes, items.Select(i => (string?)i.Attribute("CommandParameter")));
        Assert.All(items, i => Assert.Equal("{Binding AddTransitionCommand}", (string?)i.Attribute("Command")));
        Assert.All(items, i => Assert.StartsWith("{x:Static l:Strings.TransitionType_", (string?)i.Attribute("Header")));
    }

    // --- fixes after the Phase 28 manual run (D1, D3, D4, O1, O2) ------------------------------------------------------

    [Fact]
    public void The_arrows_step_to_the_next_tenth_of_a_second_from_any_value_and_a_typed_value_is_never_a_step()
    {
        _projects.Current.Settings.FrameRate = FrameRate.Fps60;
        var (a, b) = SplitVideo(at: 600, rate: FrameRate.Fps60);
        var t = AddSelected(a, b, TransitionRules.CrossDissolve);                       // 60 frames

        Inspector.TransitionSeconds = 0.75m;                                              // typed: 45 frames
        Assert.Equal(F(45), t.Duration);
        Inspector.StepTransitionSecondsCommand.Execute(1);                               // up: 0.8 s, not 0.9 s (O1)
        Assert.Equal(F(48), t.Duration);
        Assert.Equal(0.8m, Inspector.TransitionSeconds);
        Inspector.TransitionSeconds = 0.75m;
        Inspector.StepTransitionSecondsCommand.Execute(-1);                              // down: 0.7 s
        Assert.Equal(F(42), t.Duration);
        Inspector.StepTransitionSecondsCommand.Execute(-1);
        Assert.Equal(F(36), t.Duration);

        Inspector.TransitionSeconds = 0.75m;
        Inspector.TransitionSeconds = 0.85m;                                              // typed 0.1 s above: 51 frames, not 0.9 s
        Assert.Equal(F(51), t.Duration);
        Assert.Equal(0.85m, Inspector.TransitionSeconds);
    }

    [Fact]
    public void The_arrows_stop_at_the_types_range_and_at_the_longest_that_fits()
    {
        _projects.Current.Settings.FrameRate = FrameRate.Fps60;
        var (a, b) = SplitVideo(at: 600, rate: FrameRate.Fps60);
        var t = AddSelected(a, b, TransitionRules.Flash);                               // 0.1–0.5 s: 6–30 frames

        Inspector.TransitionSeconds = 0.5m;
        Inspector.StepTransitionSecondsCommand.Execute(1);
        Assert.Equal(F(30), t.Duration);                                                  // nothing beyond the maximum
        Inspector.TransitionSeconds = 0.1m;
        Inspector.StepTransitionSecondsCommand.Execute(-1);
        Assert.Equal(F(6), t.Duration);                                                   // nothing below the minimum
        Inspector.TransitionSeconds = 0.13m;                                              // 8 frames: the next mark down is 0.1 s
        Inspector.StepTransitionSecondsCommand.Execute(-1);
        Assert.Equal(F(6), t.Duration);
    }

    [Fact]
    public void An_arrow_step_up_to_the_longest_that_fits_is_clamped_there()
    {
        var (a, b) = SplitVideo(seconds: 4, at: 95);                                     // B has 5 frames: a dip of 10 at most
        var t = AddSelected(a, b, TransitionRules.DipToBlack);
        Assert.Equal(F(10), t.Duration);
        Inspector.TransitionSeconds = 0.28m;                                              // 7 frames
        Inspector.StepTransitionSecondsCommand.Execute(1);                               // 0.3 s → 8 frames
        Assert.Equal(F(8), t.Duration);
        Inspector.StepTransitionSecondsCommand.Execute(1);                               // 0.4 s → 10 frames: the longest
        Assert.Equal(F(10), t.Duration);
        Inspector.StepTransitionSecondsCommand.Execute(1);
        Assert.Equal(F(10), t.Duration);
    }

    [Fact]
    public void The_seconds_fields_spinner_steps_through_the_command()
    {
        var (a, b) = SplitVideo();
        var t = AddSelected(a, b, TransitionRules.Zoom);                                 // 8 frames at 25 = 0.32 s
        var view = new InspectorView { DataContext = Inspector };
        var field = view.GetLogicalDescendants().OfType<CommitNumericUpDown>().Single(n => n.Name == "TransitionSecondsField");
        Assert.Same(Inspector.StepTransitionSecondsCommand, field.StepCommand);

        Assert.True(field.Step(Avalonia.Controls.SpinDirection.Increase));               // 0.4 s → 10 frames
        Assert.Equal(F(10), t.Duration);
        Assert.True(field.Step(Avalonia.Controls.SpinDirection.Decrease));               // 0.3 s → 8 frames (7.5 up)
        Assert.Equal(F(8), t.Duration);

        var plain = new CommitNumericUpDown();                                            // without a command: the base step
        Assert.False(plain.Step(Avalonia.Controls.SpinDirection.Increase));
    }

    [Fact]
    public void A_longest_shorter_than_the_stored_duration_is_not_shown_as_a_limit()
    {
        var (a, b) = SplitVideo();
        AddSelected(a, b, TransitionRules.CrossDissolve);
        Assert.Equal(string.Format(Strings.Transition_Longest, TransitionNames.Duration(201, Rate)), Inspector.TransitionLimitDisplay);

        foreach (var asset in _projects.Current.MediaAssets) asset.Metadata = null;     // the source's length unknown (O2)
        Timeline.OnTransitionPressed(ZoneVm());

        Assert.Equal(Strings.Transition_NoLonger, Inspector.TransitionLimitDisplay);
        Assert.Equal(1m, Inspector.MaxTransitionSeconds);                                 // the stored 25 frames stay reachable
    }

    [Fact]
    public void The_crossfade_shows_no_limit_below_its_length_either()
    {
        var music = Asset(20, FrameRate.Fps25, MediaKind.Audio);
        var a1 = _projects.Current.Timeline.AudioTracks[0];
        Assert.True(_edit.AddClip(music.Id, a1.Id, F(0)).Success);
        Assert.True(_edit.Split(F(100), new[] { a1.Clips[0].Id }).Success);
        SelectBoth(a1.Clips[0], a1.Clips[1]);
        Timeline.AddDissolveCommand.Execute(null);

        music.Metadata = null;
        Timeline.OnTransitionPressed(Timeline.Tracks.SelectMany(t => t.Transitions).Single());

        Assert.Equal(Strings.Transition_NoLonger, Inspector.DissolveLimitDisplay);
    }

    [Fact]
    public void A_disabled_type_shows_its_reason_on_hover()
    {
        var types = InspectorXaml().Descendants(Ui + "ItemsControl").Single(i => (string?)i.Attribute(X + "Name") == "TransitionTypeButtons");
        var button = types.Descendants(Ui + "ToggleButton").Single();

        Assert.Equal("{Binding IsAvailable}", (string?)button.Attribute("IsEnabled"));
        Assert.Equal("{Binding Tip}", (string?)button.Attribute("ToolTip.Tip"));
        Assert.Equal("True", (string?)button.Attribute("ToolTip.ShowOnDisabled"));      // Avalonia hides it otherwise (D3)
    }

    [Fact]
    public void The_transition_section_wraps_in_the_inspectors_width()
    {
        var section = InspectorXaml().Descendants(Ui + "StackPanel").Single(p => (string?)p.Attribute(X + "Name") == "TransitionLibrarySection");

        // The type and direction buttons wrap in the column after the labels: no row gives them their own width (D1).
        foreach (var name in new[] { "TransitionTypeButtons", "TransitionDirectionButtons" })
        {
            var items = section.Descendants(Ui + "ItemsControl").Single(i => (string?)i.Attribute(X + "Name") == name);
            Assert.Null(items.Attribute("MaxWidth"));
            Assert.Equal("1", (string?)items.Attribute("Grid.Column"));
            Assert.Equal(Ui + "Grid", items.Parent!.Name);
            Assert.Equal("124,*", (string?)items.Parent.Attribute("ColumnDefinitions"));
        }
        Assert.DoesNotContain(section.Descendants(Ui + "StackPanel"),
            p => (string?)p.Attribute("Orientation") == "Horizontal" && p.Descendants(Ui + "ItemsControl").Any());

        // The Remove button's label («Удалить перекрёстное затухание») wraps.
        var remove = InspectorXaml().Descendants(Ui + "Button").Single(b => (string?)b.Attribute(X + "Name") == "RemoveTransitionButton");
        var label = Assert.Single(remove.Elements(Ui + "TextBlock"));
        Assert.Equal(("{Binding RemoveTransitionLabel}", "Wrap"), ((string?)label.Attribute("Text"), (string?)label.Attribute("TextWrapping")));
    }

    [Fact]
    public void A_zones_mark_is_a_hit_target_over_the_trim_handles_and_names_the_type()
    {
        var xaml = Xaml("TimelineView.axaml");
        var zones = xaml.Descendants(Ui + "ItemsControl").Single(i => (string?)i.Attribute("ItemsSource") == "{Binding Transitions}");
        Assert.Null(zones.Attribute("IsHitTestVisible"));                                 // its children can be hit
        var zone = zones.Descendants(Ui + "Border").Single(b => ((string?)b.Attribute("Classes"))?.Contains("dissolve") == true);
        Assert.Equal("False", (string?)zone.Attribute("IsHitTestVisible"));               // the zone keeps the handles reachable
        var mark = zones.Descendants(Ui + "Border").Single(b => (string?)b.Attribute("Tag") == "TransitionMark");
        Assert.Null(mark.Attribute("IsHitTestVisible"));
        Assert.NotNull(mark.Attribute("Background"));                                     // a hit target where it is drawn
        Assert.Equal(("14", "14"), ((string?)mark.Attribute("Width"), (string?)mark.Attribute("Height")));
        Assert.Equal("{Binding TypeName}", (string?)mark.Attribute("ToolTip.Tip"));

        var (a, b) = SplitVideo();
        AddSelected(a, b, TransitionRules.Push);
        Assert.Equal("Push", ZoneVm().TypeName);
        Inspector.SetTransitionTypeCommand.Execute(TransitionRules.Flash);
        Assert.Equal("Flash", ZoneVm().TypeName);
    }

    private static readonly System.Xml.Linq.XNamespace Ui = "https://github.com/avaloniaui";
    private static readonly System.Xml.Linq.XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static System.Xml.Linq.XDocument InspectorXaml() => Xaml("InspectorView.axaml");

    private static System.Xml.Linq.XDocument Xaml(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AiVideoEditor.sln"))) root = root.Parent;
        return System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName, "src", "UI", "Views", "Panels", file));
    }

    private sealed class NoAnalysis : IMediaAnalysisService
    {
        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(MediaAnalysisResult.Failure(MediaAnalysisOutcome.ProbeToolUnavailable, "test"));
    }

    private sealed class NoImport : IMediaImportService
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>();
        public Task<MediaImportBatchResult> ImportManyAsync(IEnumerable<string> filePaths, CancellationToken ct = default) =>
            Task.FromResult(new MediaImportBatchResult());
    }
}
