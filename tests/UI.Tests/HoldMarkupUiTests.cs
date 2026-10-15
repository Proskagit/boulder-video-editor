using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Avalonia;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 19 Step 19.5 (D034 §1, §4, §7) through the real shell wiring with a fake model and a fake markup frame: the
/// Inspector's "Holds" section (video clips only, one undo step per style change), the markup mode's guards, a click →
/// candidate with its visible alternatives (masks over 2 % hidden; none → a message), Tab / the wheel, Shift+click
/// refining to one mask, Enter → one undoable hold with its samples and source time, Esc, selecting and deleting a hold
/// (Delete never reaches the timeline in the mode), leaving the mode on seek / another clip, a missing model, the CPU
/// message; the overlay's mapping of a click through crop and rotation.
/// </summary>
public sealed class HoldMarkupUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const int W = 200, H = 100;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly FakeSegmentation _model = new();
    private readonly FakeFrames _frames = new();
    private readonly EditingLock _lock = new();
    private readonly MainWindowViewModel _vm;

    public HoldMarkupUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, new FakeAudioDecoder(), new FakeAudioOutput());
        var analysis = new MediaAnalysisCoordinator(new NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);

        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance,
            holdMarkup: new HoldMarkupViewModel(_edit, _projects, _status, _model, _frames, _lock));
        _undo.StateChanged += (_, _) => _history++;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HoldMarkupViewModel Markup => _vm.HoldMarkup!;

    private MediaAsset Asset(string name, MediaKind kind)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", name),
            Kind = kind,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate,
                Width = kind == MediaKind.Audio ? null : W, Height = kind == MediaKind.Audio ? null : H,
                AudioCodec = "aac"
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        return asset;
    }

    private VideoClip AddVideo(string name = "v.mp4")
    {
        var asset = Asset(name, MediaKind.Video);
        _vm.Timeline.AddMedia(asset);
        return _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single(c => c.MediaAssetId == asset.Id);
    }

    private async Task<VideoClip> StartOnClip()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        Assert.True(Markup.StartCommand.CanExecute(null));
        await Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsActive);
        Assert.False(Markup.IsPreparing);
        return clip;
    }

    /// <summary>History changes so far: each executed command is one (undo / redo too).</summary>
    private int _history;
    private int UndoSteps() => _history;

    // --- the Inspector section ---------------------------------------------------------------------------------------

    [Fact]
    public void The_section_shows_for_a_video_clip_only_and_each_style_change_is_one_undo_step()
    {
        _vm.Timeline.AddMedia(Asset("m.wav", MediaKind.Audio));
        Assert.False(Markup.HasClip);
        var clip = AddVideo();
        Assert.True(Markup.HasClip);
        Assert.Equal(0, Markup.HoldCount);
        Assert.Equal(4m, Markup.StrokeWidth);
        var steps = UndoSteps();

        Markup.StrokeWidth = 6;
        Assert.Equal(6, clip.HoldMarkup!.StrokeWidth1080);
        Markup.PaletteSelection = RouteColor.Palette[7];
        Assert.Equal((HoldColorMode.Manual, RouteColor.Palette[7].Hex), (clip.HoldMarkup.ColorMode, clip.HoldMarkup.ColorHex));
        Assert.True(Markup.IsManualColor);
        Markup.KeylineIndex = (int)KeylineMode.On;
        Assert.True(clip.HoldMarkup.KeylineActive);
        Assert.Equal(steps + 3, UndoSteps());

        _undo.Undo();
        Assert.Equal((int)KeylineMode.Auto, Markup.KeylineIndex);   // the section follows undo
        Markup.ColorHex = "#12";                                    // incomplete: nothing applied
        Assert.Equal(RouteColor.Palette[7].Hex, clip.HoldMarkup!.ColorHex);
    }

    // --- markup mode -------------------------------------------------------------------------------------------------

    [Fact]
    public void Markup_starts_only_paused_on_the_clip_under_the_playhead()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineEnd + MediaTime.FromSeconds(1));
        Assert.False(Markup.StartCommand.CanExecute(null));
        _vm.Timeline.SetPlayhead(clip.TimelineStart);
        Assert.True(Markup.StartCommand.CanExecute(null));
        Markup.SetClip(null);
        Assert.False(Markup.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_click_gives_the_best_visible_mask_with_its_alternatives_and_enter_adds_one_undoable_hold()
    {
        var clip = await StartOnClip();
        Assert.Equal(new MediaTime(MediaTime.FromSeconds(1).Ticks), _frames.LastPlayhead - clip.TimelineStart);

        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: false);
        Assert.True(Markup.HasCandidate);
        Assert.Equal("Outline 1 of 2", Markup.AlternativeText);     // the full-frame mask is hidden
        var best = Markup.CandidateContour;
        Assert.All(best, p => Assert.InRange(p.X * W, 90, 110));     // the 18 × 18 square (score 0.9) first

        Markup.NextAlternativeCommand.Execute(null);
        await WaitFor(() => Markup.AlternativeText == "Outline 2 of 2");
        await WaitFor(() => !Markup.CandidateContour.SequenceEqual(best));
        Markup.OnWheel(-1);
        await WaitFor(() => Markup.AlternativeText == "Outline 1 of 2");
        await WaitFor(() => Markup.CandidateContour.SequenceEqual(best));

        var steps = UndoSteps();
        await Markup.ConfirmCommand.ExecuteAsync(null);
        var hold = Assert.Single(clip.HoldMarkup!.Holds);
        Assert.False(Markup.HasCandidate);
        Assert.Equal(best.ToArray(), hold.Points.ToArray());
        Assert.Equal(MediaTime.FromSeconds(1), hold.SourceTime);
        Assert.True(hold.SurfaceLab.DistanceTo(RouteColor.SrgbToOkLab(200, 30, 40)) < 0.02);
        Assert.True(hold.WallLab.DistanceTo(RouteColor.SrgbToOkLab(230, 220, 200)) < 0.02);
        Assert.Equal(steps + 1, UndoSteps());
        Assert.Equal(1, Markup.HoldCount);

        _undo.Undo();
        Assert.Empty(clip.HoldMarkup?.Holds ?? ImmutableArray<Hold>.Empty);
        Assert.True(Markup.IsActive);                                  // undo doesn't leave the mode
    }

    [Fact]
    public async Task A_click_right_after_enter_never_drops_the_hold_being_confirmed()
    {
        // Found in the real-app run (Step 19.8): the next click came while the confirm was still sampling the colours.
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        var confirm = Markup.ConfirmCommand.ExecuteAsync(null);                 // not awaited
        var click = Markup.OnClickAsync(new NormalizedPoint(0.2, 0.5), false);   // at once
        Markup.EscapeCommand.Execute(null);                                     // Enter came first: ignored for the hold
        await Task.WhenAll(confirm, click);
        Assert.Single(clip.HoldMarkup!.Holds);
        Assert.True(Markup.HasCandidate);                                       // the new click's candidate
    }

    [Fact]
    public async Task Shift_click_refines_the_same_candidate_to_one_mask_and_esc_discards_it()
    {
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: false);
        await Markup.OnClickAsync(new NormalizedPoint(0.52, 0.5), additive: true);
        Assert.Equal(2, _model.LastPoints!.Count);
        Assert.Equal(new PointD(104, 50), _model.LastPoints[1]);
        Assert.True(Markup.HasCandidate);
        Assert.Null(Markup.AlternativeText);                           // one mask, no alternatives

        Markup.EscapeCommand.Execute(null);
        Assert.False(Markup.HasCandidate);
        Assert.True(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);                                  // nothing changed the project

        await Markup.OnClickAsync(new NormalizedPoint(0.3, 0.5), additive: false);
        Assert.Single(_model.LastPoints);                              // a plain click starts over
    }

    [Fact]
    public async Task A_refinement_without_an_allowed_mask_keeps_the_previous_candidate_and_drops_only_the_new_point()
    {
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: false);
        var contour = Markup.CandidateContour.ToArray();

        Markup.NextAlternativeCommand.Execute(null);
        await WaitFor(() => Markup.AlternativeText == "Outline 2 of 2");
        contour = Markup.CandidateContour.ToArray();
        var history = _history;

        _model.OnlyWalls = true;                                       // the refined prompt: every mask over 2 %
        await Markup.OnClickAsync(new NormalizedPoint(0.52, 0.5), additive: true);
        Assert.Equal(2, _model.LastPoints!.Count);
        Assert.True(Markup.HasCandidate);                              // kept as it was
        Assert.Equal(contour, Markup.CandidateContour.ToArray());
        Assert.Equal("Outline 2 of 2", Markup.AlternativeText);
        Assert.Equal(HoldMarkupViewModel.RefineRejectedMessage, _status.Message);
        Assert.Null(clip.HoldMarkup);                                  // nothing confirmed, nothing changed
        Assert.Equal(history, _history);

        _model.OnlyWalls = false;                                      // the next refinement starts from the first point only
        await Markup.OnClickAsync(new NormalizedPoint(0.48, 0.5), additive: true);
        Assert.Equal(new[] { new PointD(100, 50), new PointD(96, 50) }, _model.LastPoints);
        Assert.Null(Markup.AlternativeText);                           // a normal refinement: one mask
        await Markup.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(clip.HoldMarkup!.Holds);

    }

    [Fact]
    public async Task A_rejected_refinement_never_confirms_on_an_early_enter_and_a_shift_click_without_a_candidate_is_a_plain_click()
    {
        var clip = await StartOnClip();
        // No candidate yet: Shift+click is a plain click; without an allowed mask there is no candidate and the usual message.
        _model.OnlyWalls = true;
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: true);
        Assert.Single(_model.LastPoints!);
        Assert.False(Markup.HasCandidate);
        Assert.Contains("No hold there", _status.Message);

        _model.OnlyWalls = false;
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: false);
        var contour = Markup.CandidateContour.ToArray();
        _model.OnlyWalls = true;
        _model.PredictDelay = TimeSpan.FromMilliseconds(150);
        var refine = Markup.OnClickAsync(new NormalizedPoint(0.52, 0.5), additive: true);
        await Markup.ConfirmCommand.ExecuteAsync(null);                // Enter while the refinement runs
        await refine;
        await Task.Delay(100);
        Assert.Null(clip.HoldMarkup);                                  // the early Enter is dropped with the refinement
        Assert.Equal(contour, Markup.CandidateContour.ToArray());
        Assert.Equal(HoldMarkupViewModel.RefineRejectedMessage, _status.Message);
    }

    [Fact]
    public async Task When_every_mask_is_larger_than_two_percent_there_is_no_candidate_and_a_message()
    {
        await StartOnClip();
        _model.OnlyWalls = true;
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), additive: false);
        Assert.False(Markup.HasCandidate);
        Assert.False(Markup.ConfirmCommand.CanExecute(null));
        Assert.Contains("2 %", _status.Message);
    }

    [Fact]
    public async Task A_click_on_a_confirmed_hold_selects_it_and_delete_removes_only_the_hold()
    {
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        await Markup.ConfirmCommand.ExecuteAsync(null);
        var hold = Assert.Single(clip.HoldMarkup!.Holds);

        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        Assert.Equal(hold.Id, Markup.SelectedHoldId);
        Assert.False(Markup.HasCandidate);

        var delete = ShortcutRouter.CommandFor(_vm, Key.Delete, KeyModifiers.None);
        Assert.Same(Markup.DeleteSelectedCommand, delete);
        Assert.Same(Markup.ConfirmCommand, ShortcutRouter.CommandFor(_vm, Key.Enter, KeyModifiers.None));
        Assert.Same(Markup.EscapeCommand, ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None));
        Assert.Same(Markup.PreviousAlternativeCommand, ShortcutRouter.CommandFor(_vm, Key.Tab, KeyModifiers.Shift));
        delete!.Execute(null);
        Assert.Empty(clip.HoldMarkup!.Holds);
        Assert.Single(_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips));   // the clip stays

        Markup.DoneCommand.Execute(null);
        Assert.Same(_vm.Timeline.DeleteSelectedCommand, ShortcutRouter.CommandFor(_vm, Key.Delete, KeyModifiers.None));
    }

    [Fact]
    public async Task A_seek_or_another_clip_leaves_the_mode_and_the_markup_stays()
    {
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        await Markup.ConfirmCommand.ExecuteAsync(null);
        await Markup.OnClickAsync(new NormalizedPoint(0.2, 0.5), false);
        Assert.True(Markup.HasCandidate);

        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(2));
        Assert.False(Markup.IsActive);
        Assert.False(Markup.HasCandidate);                             // the candidate is discarded
        Assert.Single(clip.HoldMarkup!.Holds);                         // the confirmed hold stays
        Assert.True(_model.FramesDisposed > 0);                         // the encoded frame released

        await Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsActive);
        AddVideo("other.mp4");                                         // selects the new clip
        Assert.False(Markup.IsActive);
    }

    [Fact]
    public async Task Play_the_source_mode_an_export_and_another_project_leave_the_mode()
    {
        var clip = await StartOnClip();
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        _vm.Preview.IsPlaying = true;                                  // Play
        Assert.False(Markup.IsActive);
        Assert.False(Markup.HasCandidate);
        Assert.False(Markup.StartCommand.CanExecute(null));
        _vm.Preview.IsPlaying = false;

        await Markup.StartCommand.ExecuteAsync(null);
        _vm.Preview.IsSourceMode = true;                               // the Source mode
        Assert.False(Markup.IsActive);
        _vm.Preview.IsSourceMode = false;

        await Markup.StartCommand.ExecuteAsync(null);
        using (_lock.Acquire())                                        // an export
        {
            Assert.False(Markup.IsActive);
            Assert.False(Markup.StartCommand.CanExecute(null));
            Markup.StrokeWidth = 9;                                    // refused, the section shows the model again
            Assert.Null(clip.HoldMarkup);
            Assert.Equal(4m, Markup.StrokeWidth);
        }

        await Markup.StartCommand.ExecuteAsync(null);
        _projects.CreateNew("Other");                                  // another project
        Assert.False(Markup.IsActive);
        Assert.False(Markup.HasClip);
    }

    [Fact]
    public async Task A_locked_track_offers_no_markup_and_locking_it_leaves_the_mode()
    {
        // Found in the real-app run (Step 19.8): the mode opened on a locked track and every confirm was refused.
        var clip = await StartOnClip();
        var track = _projects.Current.Timeline.VideoTracks.Single(t => t.Clips.Contains(clip));
        Assert.True(_edit.SetTrackLocked(track.Id, true).Success);
        Assert.False(Markup.IsActive);
        Assert.False(Markup.StartCommand.CanExecute(null));
        Assert.True(_edit.SetTrackLocked(track.Id, false).Success);
        Assert.True(Markup.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Esc_while_the_frame_is_prepared_cancels_without_a_candidate_or_an_edit()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart);
        var dirty = _projects.Current.IsDirty;
        var history = _history;
        _model.Gate = new TaskCompletionSource();
        var start = Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsPreparing);
        Assert.Same(Markup.EscapeCommand, ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None));
        Markup.EscapeCommand.Execute(null);
        Assert.False(Markup.IsActive);
        _model.Gate.SetResult();
        await start;
        Assert.False(Markup.IsActive);                                 // the late continuation changes nothing
        Assert.False(Markup.HasCandidate);
        Assert.Equal((dirty, history), (_projects.Current.IsDirty, _history));
        Assert.Null(_frames.LastPlayhead);
    }

    [Fact]
    public async Task Enter_pressed_before_the_candidate_appears_confirms_it_when_it_does()
    {
        // Found in the real-app run (Step 19.8): a quick Enter while the model was still predicting was swallowed.
        var clip = await StartOnClip();
        _model.PredictDelay = TimeSpan.FromMilliseconds(200);
        var click = Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        Assert.True(Markup.ConfirmCommand.CanExecute(null));           // routed and accepted while predicting
        await Markup.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(clip.HoldMarkup);                                  // not yet: no candidate
        await click;
        await WaitFor(() => clip.HoldMarkup is { Holds.Length: 1 } && !Markup.HasCandidate);   // added, then cleared

        // A new click before the prediction ends forgets the early Enter.
        var first = Markup.OnClickAsync(new NormalizedPoint(0.2, 0.5), false);
        await Markup.ConfirmCommand.ExecuteAsync(null);
        var second = Markup.OnClickAsync(new NormalizedPoint(0.8, 0.5), false);
        await Task.WhenAll(first, second);
        await Task.Delay(100);
        Assert.Single(clip.HoldMarkup!.Holds);
        Assert.True(Markup.HasCandidate);
    }

    [Fact]
    public async Task A_missing_model_says_so_and_never_enters_the_mode()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart);
        _model.Status = new HoldModelStatus(HoldModelState.Missing, "The hold model is missing: encoder.onnx in C:\\models.");
        await Markup.StartCommand.ExecuteAsync(null);
        Assert.False(Markup.IsActive);
        Assert.Contains("encoder.onnx", _status.Message);
        Assert.Null(_frames.LastPlayhead);
    }

    [Fact]
    public async Task On_the_cpu_the_user_is_told_processing_may_be_slower()
    {
        _model.Device = InferenceDevice.Cpu;
        await StartOnClip();
        Assert.Equal(HoldMarkupViewModel.CpuMessage, _status.Message);
    }

    // --- the overlay's mapping ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0.1, 0.2, 0, 0)]
    [InlineData(0.1, 0.2, 90, 0.5)]
    [InlineData(0, 0, 30, 1.5)]
    public void A_view_point_maps_to_the_source_and_back_through_crop_and_rotation(double cropLeft, double cropTop, double rotation, double offset)
    {
        var canvas = new FrameSize(1920, 1080);
        var visual = VisualProperties.Default with
        {
            Crop = new CropRect(cropLeft, cropTop, 0.05, 0),
            RotationDegrees = rotation,
            Scale = 0.8,
            PositionX = 100 * offset
        };
        var geometry = CompositionMath.Layout(canvas, new FrameSize(3840, 2160), visual);
        var view = new Size(800, 600);
        var n = new NormalizedPoint(0.4, 0.6);
        var p = HoldMarkupOverlay.ToView(geometry, canvas, view, n);
        var back = HoldMarkupOverlay.ToSource(geometry, canvas, view, p)!.Value;
        Assert.Equal(n.X, back.X, 9);
        Assert.Equal(n.Y, back.Y, 9);

        // Outside the visible (cropped) picture: no point.
        var cropped = HoldMarkupOverlay.ToView(geometry, canvas, view, new NormalizedPoint(0.98, 0.5));
        Assert.Null(HoldMarkupOverlay.ToSource(geometry, canvas, view, cropped));
    }

    // --- helpers -----------------------------------------------------------------------------------------------------

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException();
            await Task.Delay(1);
        }
    }

    /// <summary>A red 18 × 18 hold in the middle of a beige wall.</summary>
    private sealed class FakeFrames : IHoldMarkupFrameSource
    {
        public MediaTime? LastPlayhead { get; private set; }

        public Task<HoldMarkupFrame> GetAsync(VideoClip clip, MediaAsset asset, MediaTime playhead, FrameRate rate, CancellationToken cancellationToken)
        {
            LastPlayhead = playhead;
            var pixels = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var i = (y * W + x) * 4;
                var hold = Math.Abs(x - 100) < 9 && Math.Abs(y - 50) < 9;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = hold ? ((byte)40, (byte)30, (byte)200, (byte)255) : ((byte)200, (byte)220, (byte)230, (byte)255);
            }
            var frame = new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(25, new TimeBase(1, 25)));
            return Task.FromResult(new HoldMarkupFrame(frame, playhead - clip.TimelineStart));
        }
    }

    /// <summary>For a click: an 18 × 18 square around it (score 0.9), the whole frame (0.95, hidden), a 6 × 6 square (0.5);
    /// for several points one more square; only walls on demand.</summary>
    private sealed class FakeSegmentation : IHoldSegmentationService
    {
        public HoldModelStatus Status { get; set; } = new(HoldModelState.Ready, null);
        public InferenceDevice Device { get; set; } = InferenceDevice.Gpu;
        public bool OnlyWalls { get; set; }
        public IReadOnlyList<PointD>? LastPoints { get; private set; }
        public int FramesDisposed { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public TimeSpan PredictDelay { get; set; }
        public event EventHandler? DeviceChanged { add { } remove { } }

        public async Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (Gate is { } gate) await gate.Task;
            progress?.Report(1);
            return Status;
        }

        public Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromResult<IHoldFrame>(new Frame(this, frame.Width, frame.Height));

        private static HoldMask Square(PointD c, int half, double score)
        {
            var p = new byte[W * H];
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                if (Math.Abs(x - (int)c.X) < half && Math.Abs(y - (int)c.Y) < half) p[y * W + x] = 1;
            return new HoldMask(W, H, p, score);
        }

        private sealed class Frame(FakeSegmentation owner, int width, int height) : IHoldFrame
        {
            public int Width => width;
            public int Height => height;

            public async Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
            {
                if (owner.PredictDelay > TimeSpan.Zero) await Task.Delay(owner.PredictDelay, cancellationToken);
                owner.LastPoints = points.ToList();
                var all = new HoldMask(W, H, Enumerable.Repeat((byte)1, W * H).ToArray(), 0.95);
                var masks = owner.OnlyWalls
                    ? ImmutableArray.Create(all, all, all)
                    : ImmutableArray.Create(Square(points[0], 9, 0.9), all, Square(points[0], 3, 0.5 + 0.1 * (points.Count - 1)));
                return new HoldMaskSet(masks);
            }

            public void Dispose() => owner.FramesDisposed++;
        }
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
