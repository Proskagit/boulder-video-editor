using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 17 Step 17.8 (D032 R12, AQ1, AQ3, AQ9, R9 in the UI): the timeline toolbar's Unlink / Unlink Audio and Link, the
/// link mark, the linked selection (Ctrl toggles the pair), Alt + edge trimming one clip alone, the Dissolve command adding
/// a crossfade on an audio cut, the Inspector's CROSSFADE section and the detached video clip's missing audio group and
/// waveform, and the editing lock — through the real shell wiring.
/// </summary>
public sealed class TimelineLinkUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public TimelineLinkUiTests()
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
    private Track A1 => _projects.Current.Timeline.AudioTracks[0];
    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private TimelineClipViewModel Vm(Clip clip) => Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip == clip);
    private List<Clip> Selected() => Timeline.Tracks.SelectMany(t => t.Clips).Where(c => c.IsSelected).Select(c => c.Clip).ToList();

    /// <summary>V1: a 20 s recording with sound at [0, 100).</summary>
    private VideoClip Recording()
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-link-ui", Guid.NewGuid().ToString("N"), "take.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080, AudioCodec = "aac" }
        };
        _projects.AddMediaAssets(new[] { asset });
        Assert.True(_edit.AddClip(asset.Id).Success);
        Assert.True(_edit.TrimClip(V1.Clips[0].Id, ClipEdge.End, F(100)).Success);
        return (VideoClip)V1.Clips[0];
    }

    private void Click(Clip clip, bool toggle = false)
    {
        if (Timeline.OnClipPressed(Vm(clip), toggle)) Timeline.OnClipClicked(Vm(clip), toggle);
    }

    [Fact]
    public void Unlink_Audio_then_Link_then_Unlink_from_the_toolbar()
    {
        var video = Recording();
        Click(video);
        Assert.Equal("Unlink Audio", Timeline.UnlinkLabel);
        Assert.True(Timeline.UnlinkCommand.CanExecute(null));
        Assert.False(Timeline.LinkCommand.CanExecute(null));

        Timeline.UnlinkCommand.Execute(null);                                            // Unlink Audio
        var sound = Assert.IsType<AudioClip>(Assert.Single(A1.Clips));
        Assert.True(video.IsAudioDetached);
        Assert.Equal("Sound unlinked to track A1.", _status.Message);
        Assert.Equal(new Clip[] { video, sound }.ToHashSet(), Selected().ToHashSet());   // both selected
        Assert.Null(Vm(video).Waveform);                                                // R12: no sound of its own
        Assert.False(Vm(video).IsLinked);

        Assert.True(Timeline.LinkCommand.CanExecute(null));
        Timeline.LinkCommand.Execute(null);
        Assert.Equal(video.LinkId, sound.LinkId);
        Assert.True(Vm(video).IsLinked);
        Assert.StartsWith("🔗 ", Vm(sound).Label);
        Assert.Equal("Unlink", Timeline.UnlinkLabel);

        Timeline.UnlinkCommand.Execute(null);                                            // Unlink
        Assert.Null(video.LinkId);
        Assert.Null(sound.LinkId);
        Assert.False(Vm(sound).IsLinked);

        _vm.Toolbar.UndoCommand.Execute(null);
        Assert.True(Vm(video).IsLinked);                                                 // the mark follows Undo
    }

    [Fact]
    public void Selecting_a_linked_clip_selects_its_partner_and_Ctrl_toggles_the_pair()
    {
        var video = Recording();
        Assert.True(_edit.UnlinkAudio(video.Id).Success);
        var sound = (AudioClip)A1.Clips[0];
        Assert.True(_edit.LinkClips(new[] { video.Id, sound.Id }).Success);

        Click(video);
        Assert.Equal(new Clip[] { video, sound }.ToHashSet(), Selected().ToHashSet());
        Assert.Equal("Video clip", Inspector.ClipTypeLabel);                          // the clicked clip is the primary

        Click(video, toggle: true);                                                      // Ctrl: the pair leaves
        Assert.Empty(Selected());
        Click(sound, toggle: true);                                                      // and comes back
        Assert.Equal(2, Selected().Count);
    }

    [Fact]
    public void Alt_edge_trims_one_clip_alone_and_a_plain_edge_trims_both()
    {
        var video = Recording();
        Assert.True(_edit.UnlinkAudio(video.Id).Success);
        var sound = (AudioClip)A1.Clips[0];
        Assert.True(_edit.LinkClips(new[] { video.Id, sound.Id }).Success);
        var pps = Timeline.PixelsPerSecond;
        double X(long frame) => frame / 25.0 * pps;

        Timeline.BeginTrim(Vm(video), ClipEdge.End, X(100));
        Timeline.UpdateGesture(X(80), null);
        Timeline.EndGesture();
        Assert.Equal((F(80), F(80)), (video.TimelineEnd, sound.TimelineEnd));

        Timeline.BeginTrim(Vm(sound), ClipEdge.End, X(80), alone: true);                 // an L cut
        Assert.Equal(new Clip[] { sound }, Selected());
        Timeline.UpdateGesture(X(90), null);
        Timeline.EndGesture();
        Assert.Equal((F(80), F(90)), (video.TimelineEnd, sound.TimelineEnd));
        Assert.True(TimelineGestureModifiers.IsTrimAlone(KeyModifiers.Alt));
        Assert.False(TimelineGestureModifiers.IsTrimAlone(KeyModifiers.Alt | KeyModifiers.Control));
    }

    [Fact]
    public void Alt_changes_only_a_linked_clips_edge_and_Shift_Alt_on_an_unlinked_clip_still_ripples()
    {
        var video = Recording();
        var other = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "aive-link-ui", Guid.NewGuid().ToString("N"), "b.mp4"),
            Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(20), FrameRate = Rate, Width = 1920, Height = 1080 }
        };
        _projects.AddMediaAssets(new[] { other });
        Assert.True(_edit.AddClip(other.Id, V1.Id, F(100)).Success);              // right after the recording
        var later = V1.Clips[1];
        var pps = Timeline.PixelsPerSecond;
        double X(long frame) => frame / 25.0 * pps;

        Timeline.BeginTrim(Vm(video), ClipEdge.End, X(100), ripple: true, alone: true);   // Shift + Alt, unlinked: a ripple
        Timeline.UpdateGesture(X(80), null);
        Timeline.EndGesture();
        Assert.Equal(F(80), video.TimelineEnd);
        Assert.Equal(F(80), later.TimelineStart);                                  // the later clip moved: rippled
    }

    [Fact]
    public void The_Dissolve_command_adds_a_crossfade_on_an_audio_cut_and_the_Inspector_names_it()
    {
        var video = Recording();
        Assert.True(_edit.Split(F(50)).Success);
        Assert.True(_edit.UnlinkAudio(V1.Clips[0].Id).Success);
        Assert.True(_edit.UnlinkAudio(V1.Clips[1].Id).Success);
        var (a, b) = (A1.Clips[0], A1.Clips[1]);

        Click(a);
        Click(b, toggle: true);
        Timeline.AddDissolveCommand.Execute(null);

        var crossfade = Assert.Single(A1.Transitions);
        Assert.Equal(TransitionRules.Crossfade, crossfade.TransitionTypeId);
        Assert.Equal("Crossfade added: 25 frames.", _status.Message);
        Assert.Equal("CROSSFADE", Inspector.TransitionHeader);
        Assert.Equal("Remove Crossfade", Inspector.RemoveTransitionLabel);
        Assert.Empty(V1.Transitions);
        Assert.NotNull(video);
    }

    [Fact]
    public void A_linked_pair_counts_once_for_Dissolve_and_Close_Gap()
    {
        var video = Recording();
        Assert.True(_edit.Split(F(50)).Success);
        var (left, right) = ((VideoClip)V1.Clips[0], (VideoClip)V1.Clips[1]);
        Assert.True(_edit.UnlinkAudio(left.Id).Success);
        Assert.True(_edit.UnlinkAudio(right.Id).Success);
        Assert.True(_edit.LinkClips(new[] { left.Id, A1.Clips[0].Id }).Success);
        Assert.True(_edit.LinkClips(new[] { right.Id, A1.Clips[1].Id }).Success);
        Assert.True(_edit.MoveClips(new[] { right.Id }, 10).Success);                   // a gap before the right pair

        Click(right);
        Assert.Equal(2, Selected().Count);
        Assert.True(Timeline.CloseGapCommand.CanExecute(null));
        Timeline.CloseGapCommand.Execute(null);
        Assert.Equal(F(50), right.TimelineStart);
        Assert.Equal(F(50), A1.Clips[1].TimelineStart);

        Click(left);
        Click(right, toggle: true);                                                      // four clips, two on the primary's track
        Assert.True(Timeline.AddDissolveCommand.CanExecute(null));
        Assert.NotNull(video);
    }

    [Fact]
    public void A_detached_video_clip_has_no_audio_group_and_the_lock_disables_the_buttons()
    {
        var video = Recording();
        Click(video);
        Assert.True(Inspector.HasAudioProperties);
        Timeline.UnlinkCommand.Execute(null);
        Click(video);
        Assert.False(Inspector.HasAudioProperties);

        Click(video);
        Click(A1.Clips[0], toggle: true);
        Assert.True(Timeline.LinkCommand.CanExecute(null));
        using (_lock.Acquire())
        {
            Assert.False(Timeline.LinkCommand.CanExecute(null));
            Assert.False(Timeline.UnlinkCommand.CanExecute(null));
        }
        Assert.True(Timeline.LinkCommand.CanExecute(null));
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
