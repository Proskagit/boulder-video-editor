using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D058 §3 (Phase 39): several selected clips in the Inspector — the five groups (the background, Fill / Fit, the volume
/// and mute, the opacity, the hold outline's style), each applied to every clip it fits as one undo step (Ctrl+Z undoes
/// it, Ctrl+Y redoes it), «—» where the clips differ, a linked pair one item, the groups that do not fit every item not
/// offered, one clip's Inspector unchanged.
/// </summary>
public sealed class ClipGroupEditingTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly FakeVideoDecoder _decoder = new();
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly MainWindowViewModel _vm;

    public ClipGroupEditingTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance, new PlaybackSettings { BufferFrames = 4 });
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);
        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _playback.DisposeAsync();

    private InspectorViewModel Inspector => _vm.Inspector;
    private ClipGroupViewModel Group => _vm.Inspector.Group;
    private Sequence Timeline => _projects.Current.Timeline;

    private MediaAsset Asset(string name, bool sound = false)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-group", Guid.NewGuid().ToString("N"), name),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = 1920, Height = 1080,
                DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080, AudioCodec = sound ? "aac" : null
            }
        };
        _decoder.Add(asset.FilePath, new FakeSource(Rate, 250));
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private IReadOnlyList<VideoClip> Videos(int count, bool sound = false)
    {
        for (var i = 0; i < count; i++) Assert.True(_edit.AddClip(Asset($"v{i}.mp4", sound).Id).Success);
        return Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().OrderBy(c => c.TimelineStart).ToList();
    }

    private void SelectAll() => _vm.Timeline.SelectAllCommand.Execute(null);

    private void Select(params Clip[] clips)
    {
        var rows = _vm.Timeline.Tracks.SelectMany(t => t.Clips).ToList();
        _vm.Timeline.OnClipPressed(rows.Single(r => r.Id == clips[0].Id), toggle: false);
        foreach (var clip in clips.Skip(1)) _vm.Timeline.OnClipPressed(rows.Single(r => r.Id == clip.Id), toggle: true);
    }

    [Fact]
    public void One_clip_keeps_its_own_inspector_and_two_or_more_show_the_group()
    {
        var clips = Videos(3);
        Select(clips[0]);
        Assert.False(Group.IsActive);
        Assert.True(Inspector.IsSingleClipSelected);

        SelectAll();
        Assert.True(Group.IsActive);
        Assert.False(Inspector.IsSingleClipSelected);
        Assert.Contains("3", Group.Heading);
        Assert.True(Group.HasBackground && Group.HasFraming && Group.HasOpacity);
        Assert.False(Group.HasAudio);                                       // files without sound
        Assert.False(Group.HasHoldStyle);                                   // nothing marked
    }

    [Fact]
    public void The_background_of_every_clip_changes_in_one_undo_step_and_redo_brings_it_back()
    {
        var clips = Videos(3);
        SelectAll();
        Assert.Equal(0, Group.BackgroundIndex);

        Group.BackgroundIndex = 1;
        Assert.All(clips, c => Assert.Equal(ClipBackground.Blur, c.Background));
        _undo.Undo();                                                        // Ctrl+Z: every clip at once
        Assert.All(clips, c => Assert.Equal(ClipBackground.Black, c.Background));
        Assert.Equal(0, Group.BackgroundIndex);
        _undo.Redo();                                                        // Ctrl+Y
        Assert.All(clips, c => Assert.Equal(ClipBackground.Blur, c.Background));
        Assert.False(_undo.CanRedo);
    }

    [Fact]
    public void Differing_values_show_as_mixed_and_a_typed_value_sets_them_all()
    {
        var clips = Videos(3);
        Assert.True(_edit.SetClipBackground(clips[0].Id, ClipBackground.Blur).Success);
        Assert.True(_edit.SetClipProperties(clips[1].Id, new ClipPropertyChange { Visual = VisualProperties.Of(clips[1])!.Value with { Opacity = 0.5 } }).Success);
        SelectAll();
        Assert.Equal(-1, Group.BackgroundIndex);                           // «—»
        Assert.Null(Group.OpacityPercent);

        Group.OpacityPercent = 80;
        Assert.All(clips, c => Assert.Equal(0.8, c.Opacity, 6));
        Assert.Equal(80, Group.OpacityPercent);
        Group.OpacityPercent = 70;                                           // the next step of the same field merges
        _undo.Undo();
        Assert.Equal(0.5, clips[1].Opacity, 6);
        Assert.Equal(1.0, clips[0].Opacity, 6);
    }

    [Fact]
    public void Fill_and_fit_frame_every_picture_in_one_step()
    {
        _projects.Current.Settings.FrameWidth = 1080;
        _projects.Current.Settings.FrameHeight = 1920;
        var clips = Videos(2);
        SelectAll();
        Group.FillFrameCommand.Execute(null);
        var filled = clips.Select(c => c.Scale).ToList();
        Assert.All(filled, s => Assert.True(s > 1.5, s.ToString()));
        Group.FitFrameCommand.Execute(null);
        Assert.All(clips, c => Assert.True(c.Scale < 1.01));
        _undo.Undo();
        Assert.Equal(filled, clips.Select(c => c.Scale));
    }

    [Fact]
    public void Volume_and_mute_go_to_each_items_sound_a_linked_pair_counting_once()
    {
        Videos(2, sound: true);
        SelectAll();
        Assert.True(Group.IsActive);
        Assert.True(Group.HasAudio);
        Assert.Equal(false, Group.IsMuted);

        Group.VolumePercent = 50;
        Group.IsMuted = true;
        var sounds = Timeline.VideoTracks.Concat(Timeline.AudioTracks).SelectMany(t => t.Clips)
            .Where(c => c is AudioClip || c is VideoClip { IsAudioDetached: false }).ToList();
        Assert.Equal(2, sounds.Count);                                       // one sound per item
        Assert.All(sounds, c => Assert.Equal(0.5, AudioProperties.Of(c)!.Value.Volume, 6));
        Assert.All(sounds, c => Assert.True(AudioProperties.Of(c)!.Value.IsMuted));
        _undo.Undo();                                                         // the mute alone
        Assert.All(sounds, c => Assert.False(AudioProperties.Of(c)!.Value.IsMuted));
        Assert.Equal(false, Group.IsMuted);
    }

    [Fact]
    public void The_outline_style_goes_to_every_marked_route_and_a_clip_without_holds_is_left_alone()
    {
        var clips = Videos(3);
        var hold = new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.1, 0.1), new NormalizedPoint(0.2, 0.1), new NormalizedPoint(0.2, 0.2)),
            RouteColor.HexToOkLab("#E53935"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero);
        clips[0].Routes = ImmutableArray.Create(new HoldMarkup { Id = Guid.NewGuid(), Holds = ImmutableArray.Create(hold), StrokeWidth1080 = 4 });
        clips[1].Routes = ImmutableArray.Create(new HoldMarkup { Id = Guid.NewGuid(), Holds = ImmutableArray.Create(hold), StrokeWidth1080 = 6 });
        _projects.NotifyTimelineChanged();
        SelectAll();
        Assert.True(Group.HasHoldStyle);
        Assert.Null(Group.StrokeWidth);                                      // 4 and 6: «—»

        Group.StrokeWidth = 8;
        Assert.Equal(8, clips[0].Routes[0].StrokeWidth1080);
        Assert.Equal(8, clips[1].Routes[0].StrokeWidth1080);
        Assert.True(clips[2].Routes.IsEmpty);                               // no route made on the unmarked clip
        Group.ColorIndex = 2;                                                // a palette colour
        Assert.All(clips.Take(2), c => Assert.Equal(HoldColorMode.Manual, c.Routes[0].ColorMode));
        Group.KeylineIndex = 1;
        Assert.All(clips.Take(2), c => Assert.Equal(KeylineMode.On, c.Routes[0].Keyline));
        _undo.Undo();
        _undo.Undo();
        _undo.Undo();
        Assert.Equal(4, clips[0].Routes[0].StrokeWidth1080);
        Assert.Equal(6, clips[1].Routes[0].StrokeWidth1080);
    }

    [Fact]
    public void Clips_of_different_kinds_are_offered_only_what_fits_every_one()
    {
        var video = Videos(1)[0];
        Assert.True(_edit.AddTextClip(MediaTime.FromSeconds(20)).Success);
        var text = Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TextClip>().Single();
        Select(video, text);
        Assert.True(Group.IsActive);
        Assert.False(Group.HasBackground);                                   // a text has no background
        Assert.False(Group.HasFraming);
        Assert.False(Group.HasAudio);
        Assert.True(Group.HasOpacity);                                        // both have an opacity
        Group.OpacityPercent = 40;
        Assert.Equal(0.4, video.Opacity, 6);
        Assert.Equal(0.4, text.Opacity, 6);
    }

    [Fact]
    public void A_refused_edit_changes_none_of_the_clips()
    {
        var clips = Videos(2);
        SelectAll();
        Timeline.VideoTracks[0].IsLocked = true;                             // the clips' track locked after the selection
        Group.BackgroundIndex = 1;
        Assert.All(clips, c => Assert.Equal(ClipBackground.Black, c.Background));
        Assert.False(_undo.CanUndo && _undo.NextUndo is GroupEditCommand);
        Assert.Equal(0, Group.BackgroundIndex);
    }
}
