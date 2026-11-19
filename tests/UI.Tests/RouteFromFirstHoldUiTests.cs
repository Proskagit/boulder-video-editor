using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using System.Windows.Input;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D072 §3 (the route from its first hold) through the real shell wiring with a stand-in model and a synthetic wall: in
/// «Разметить зацепки» with «Предлагать трассу» on, the first click on a hold of a route without holds opens the analysis
/// (only Esc acts meanwhile) → the route's holds of that colour are proposed on the markup frame itself → the review (a
/// rejected proposal stays rejected, the tolerance re-selects without the model, a missed hold added by a click + Enter) →
/// «Принять» = one <c>AddHolds</c> step at the markup frame's source time, the markup goes on; «Готово» accepts what is shown;
/// «Отменить», «Отмена» in the window, a click beside any hold and the check box off change nothing and leave the manual markup.
/// </summary>
public sealed class RouteFromFirstHoldUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const int W = 480, H = 270;
    private static readonly MediaTime T = MediaTime.FromSeconds(6);

    private static readonly (byte R, byte G, byte B) Red = (200, 35, 40);
    private static readonly (byte R, byte G, byte B) Blue = (40, 70, 200);
    private static readonly (int X, int Y, int R)[] RedHolds = { (60, 60, 12), (160, 200, 10), (300, 80, 14), (420, 200, 8) };
    private static readonly (int X, int Y, int R) BlueHold = (380, 40, 10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly PlaybackService _playback;
    private readonly StatusService _status = new();
    private readonly FloodSegmentation _model = new();
    private readonly InMemoryUserSettings _settings = new();
    private readonly MainWindowViewModel _vm;
    private int _history;

    public RouteFromFirstHoldUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(new FakeVideoDecoder(), new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, new FakeAudioDecoder(), new FakeAudioOutput());
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);

        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance,
            holdMarkup: new HoldMarkupViewModel(_edit, _projects, _status, _model, new WallFrames(), settings: _settings));
        _undo.StateChanged += (_, _) => _history++;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HoldMarkupViewModel Markup => _vm.HoldMarkup!;

    [Fact]
    public async Task The_first_click_proposes_the_route_on_the_markup_frame_and_Accept_adds_it_in_one_step()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Assert.Equal(_selected, _history);                                    // the review itself: no step
        Assert.Equal(RedHolds.Length, Markup.Proposals.Count);                // the four red holds, not the blue one
        Assert.Null(clip.HoldMarkup);                                         // nothing yet
        Assert.Equal(T, _vm.Timeline.Playhead);                               // no frame swap, no seek

        var steps = _history;
        await Markup.AcceptRouteCommand.ExecuteAsync(null);

        Assert.Equal(steps + 1, _history);                                   // one AddHolds
        var holds = clip.HoldMarkup!.Holds;
        Assert.Equal(RedHolds.Length, holds.Length);
        Assert.All(holds, h => Assert.Equal(T - clip.TimelineStart, h.SourceTime));   // at the markup frame
        Assert.All(RedHolds, r => Assert.Single(holds, h => Inside(h.Points, r)));
        Assert.True(Markup.IsActive);                                         // the markup goes on
        Assert.False(Markup.IsRouteSelection);
        Assert.Empty(Markup.Proposals);
        Assert.Equal(T, _vm.Timeline.Playhead);

        await Markup.OnClickAsync(At(BlueHold), false);                      // the next click: the manual markup
        Assert.True(Markup.HasCandidate);
        Assert.False(Markup.IsRouteSelection);
    }

    [Fact]
    public async Task Undo_restores_the_clip_whole_and_a_route_with_holds_is_never_analysed_again()
    {
        var clip = await ReviewOn(RedHolds[0]);
        await Markup.AcceptRouteCommand.ExecuteAsync(null);
        var added = clip.HoldMarkup!;
        Markup.EscapeCommand.Execute(null);                                   // leaves the mode
        Assert.False(Markup.IsActive);

        _undo.Undo();
        Assert.Null(clip.HoldMarkup);
        _undo.Redo();
        Assert.Equal(added.Holds.Select(h => h.Id), clip.HoldMarkup!.Holds.Select(h => h.Id));

        await StartMarkup();                                                  // a new session on the route with holds
        var calls = _model.Calls;
        await Markup.OnClickAsync(At(BlueHold), false);
        Assert.False(Markup.IsRouteSelection);
        Assert.True(Markup.HasCandidate);
        Assert.Equal(calls + 1, _model.Calls);                               // one prediction: no analysis
    }

    [Fact]
    public async Task The_review_rejects_widens_and_adds_by_click_before_the_one_step()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Markup.SelectedProposalIndex = 1;
        var rejected = Markup.SelectedProposal!.Proposal.Id;
        Markup.RejectOrDeleteCommand.Execute(null);                           // Delete
        Assert.Equal(RedHolds.Length - 1, Markup.Proposals.Count);
        Markup.RouteTolerance = 0.2m;                                       // re-selects; the rejection stays
        Assert.DoesNotContain(Markup.Proposals, p => p.Proposal.Id == rejected);
        var calls = _model.Calls;
        Markup.RouteTolerance = 0.05m;
        Assert.Equal(calls, _model.Calls);                                   // no model call

        await Markup.OnClickAsync(At(BlueHold), false);                      // a missed hold: a candidate, Enter adds it
        Assert.True(Markup.HasCandidate);
        await Markup.ConfirmOrAcceptCommand.ExecuteAsync(null);
        Assert.Equal(RedHolds.Length, Markup.Proposals.Count);
        Assert.Null(clip.HoldMarkup);                                         // the clip unchanged until Accept

        var steps = _history;
        await Markup.AcceptRouteCommand.ExecuteAsync(null);
        Assert.Equal(steps + 1, _history);
        Assert.Equal(RedHolds.Length, clip.HoldMarkup!.Holds.Length);
        Assert.Single(clip.HoldMarkup.Holds, h => Inside(h.Points, BlueHold));
        Assert.DoesNotContain(clip.HoldMarkup.Holds, h => h.Id == rejected);
    }

    [Fact]
    public async Task Esc_keeps_the_review_and_Discard_drops_it_leaving_the_manual_markup()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Markup.EscapeCommand.Execute(null);
        Assert.True(Markup.IsRouteReview);                                   // the review stays as it is
        Assert.Equal(RedHolds.Length, Markup.Proposals.Count);

        Markup.CancelRouteCommand.Execute(null);                              // «Отменить»
        Assert.False(Markup.IsRouteSelection);
        Assert.Empty(Markup.Proposals);
        Assert.True(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);

        await Markup.OnClickAsync(At(RedHolds[1]), false);                   // once per session: now a manual candidate
        Assert.True(Markup.HasCandidate);
        Assert.False(Markup.IsRouteSelection);
    }

    [Fact]
    public async Task Done_accepts_the_proposals_shown_and_lays_out_the_route()
    {
        var clip = await ReviewOn(RedHolds[0]);
        var steps = _history;
        await Markup.DoneCommand.ExecuteAsync(null);
        Assert.False(Markup.IsActive);
        Assert.Equal(RedHolds.Length, clip.HoldMarkup!.Holds.Length);
        Assert.Equal(steps + 2, _history);                                   // the holds, then the intro and final
        Assert.NotNull(clip.HoldMarkup.Animation);
    }

    [Fact]
    public async Task Cancel_in_the_analysis_window_changes_nothing_and_only_Esc_acts_meanwhile()
    {
        var clip = await StartMarkup();
        _model.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var click = Markup.OnClickAsync(At(RedHolds[0]), false);
        Assert.True(Markup.IsRouteAnalyzing);                                // the window is shown
        Assert.Same(ShortcutRouter.Swallowed, ShortcutRouter.CommandFor(_vm, Key.Space, KeyModifiers.None));   // no Play
        Assert.Same(ShortcutRouter.Swallowed, ShortcutRouter.CommandFor(_vm, Key.Z, KeyModifiers.Control));     // no Undo
        Assert.Same(Markup.CancelRouteAnalysisCommand, ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None));
        await Markup.OnClickAsync(At(RedHolds[1]), false);                   // a click meanwhile: nothing

        Markup.CancelRouteAnalysisCommand.Execute(null);
        Assert.False(Markup.IsRouteAnalyzing);
        _model.Gate.SetResult();
        _model.Gate = null;
        await click;

        Assert.False(Markup.IsRouteSelection);
        Assert.Empty(Markup.Proposals);
        Assert.True(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
        await Markup.OnClickAsync(At(RedHolds[1]), false);                   // the markup goes on by hand
        Assert.True(Markup.HasCandidate);
    }

    [Fact]
    public async Task The_reviews_keys_accept_reject_all_remove_and_step_through_the_proposals()
    {
        var clip = await ReviewOn(RedHolds[0]);
        ICommand? Key(Key key, KeyModifiers modifiers = KeyModifiers.None) => ShortcutRouter.CommandFor(_vm, key, modifiers);

        Assert.Same(Markup.NextItemCommand, Key(Avalonia.Input.Key.Tab));
        var before = Markup.SelectedProposalIndex;
        Key(Avalonia.Input.Key.Tab)!.Execute(null);                         // Tab: the next proposal
        Assert.Equal((before + 1) % RedHolds.Length, Markup.SelectedProposalIndex);

        var removed = Markup.SelectedProposal!.Proposal.Id;
        Assert.Same(Markup.RejectOrDeleteCommand, Key(Avalonia.Input.Key.Delete));
        Key(Avalonia.Input.Key.Delete)!.Execute(null);                      // Delete: that proposal out
        Assert.Equal(RedHolds.Length - 1, Markup.Proposals.Count);
        Assert.DoesNotContain(Markup.Proposals, p => p.Proposal.Id == removed);
        Assert.Null(clip.HoldMarkup);

        Assert.Same(Markup.AcceptRouteCommand, Key(Avalonia.Input.Key.A, KeyModifiers.Control));
        var steps = _history;
        await Markup.AcceptRouteCommand.ExecuteAsync(null);                  // Ctrl+A: «Принять»
        Assert.Equal(steps + 1, _history);
        Assert.Equal(RedHolds.Length - 1, clip.HoldMarkup!.Holds.Length);
        Assert.DoesNotContain(clip.HoldMarkup.Holds, h => h.Id == removed);
    }

    [Fact]
    public async Task Ctrl_D_removes_every_proposal_and_nothing_is_left_to_accept()
    {
        var clip = await ReviewOn(RedHolds[0]);
        var command = ShortcutRouter.CommandFor(_vm, Key.D, KeyModifiers.Control);
        Assert.Same(Markup.RejectAllProposalsCommand, command);
        command!.Execute(null);
        Assert.Empty(Markup.Proposals);
        Markup.RouteTolerance = 0.2m;                                       // the removed never come back
        Assert.Empty(Markup.Proposals);
        Assert.False(Markup.AcceptRouteCommand.CanExecute(null));
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
    }

    [Fact]
    public async Task A_click_on_a_proposal_selects_it_without_a_candidate()
    {
        await ReviewOn(RedHolds[0]);
        var calls = _model.Calls;
        await Markup.OnClickAsync(At(RedHolds[2]), false);
        Assert.False(Markup.HasCandidate);
        Assert.Equal(calls, _model.Calls);                                   // no model call
        Assert.True(Inside(Markup.SelectedProposal!.Contour, RedHolds[2]));
    }

    [Fact]
    public async Task A_failed_analysis_turns_the_click_into_a_manual_candidate_at_that_point()
    {
        var clip = await StartMarkup();
        _model.ThrowNext = true;                                             // the analysis' first prediction fails
        await Markup.OnClickAsync(At(RedHolds[0]), false);

        Assert.False(Markup.IsRouteSelection);
        Assert.Contains("model failure", _status.Message);
        Assert.True(Markup.HasCandidate);                                    // the click never lost
        Assert.True(Inside(Markup.CandidateContour, RedHolds[0]));
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
    }

    [Fact]
    public async Task A_seek_during_the_review_leaves_the_mode_and_drops_the_proposals()
    {
        var clip = await ReviewOn(RedHolds[0]);
        _vm.Timeline.SetPlayhead(T + MediaTime.FromSeconds(1));
        Assert.False(Markup.IsActive);
        Assert.False(Markup.IsRouteSelection);
        Assert.Empty(Markup.Proposals);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
    }

    [Fact]
    public async Task Leaving_the_mode_during_the_analysis_changes_nothing_and_throws_nothing()
    {
        var clip = await StartMarkup();
        _model.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var click = Markup.OnClickAsync(At(RedHolds[0]), false);
        Assert.True(Markup.IsRouteAnalyzing);
        Markup.Exit();                                                        // Play, a seek, another clip
        Assert.False(Markup.IsRouteAnalyzing);
        _model.Gate.SetResult();                                              // the search goes on on a disposed frame
        _model.Gate = null;
        await click;                                                          // no exception reaches the view

        Assert.False(Markup.IsActive);
        Assert.False(Markup.IsRouteSelection);
        Assert.Empty(Markup.Proposals);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
    }

    [Fact]
    public async Task A_click_beside_any_hold_says_so_and_leaves_the_manual_markup()
    {
        var clip = await StartMarkup();
        await Markup.OnClickAsync(WallPoint(), false);                        // the wall: no control hold
        Assert.False(Markup.IsRouteSelection);
        Assert.False(Markup.IsRouteAnalyzing);
        Assert.True(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
        await Markup.OnClickAsync(At(RedHolds[0]), false);                   // tried once: now a manual candidate
        Assert.True(Markup.HasCandidate);
        Assert.False(Markup.IsRouteSelection);
    }

    [Fact]
    public async Task With_the_check_box_off_the_first_click_is_a_manual_candidate()
    {
        Markup.IsRouteProposalEnabled = false;
        Assert.False(_settings.Current.RouteProposalsEnabled);
        await StartMarkup();
        var calls = _model.Calls;
        await Markup.OnClickAsync(At(RedHolds[0]), false);
        Assert.True(Markup.HasCandidate);
        Assert.False(Markup.IsRouteSelection);
        Assert.Equal(calls + 1, _model.Calls);
    }

    [Fact]
    public async Task A_shift_click_never_starts_the_analysis()
    {
        await StartMarkup();
        await Markup.OnClickAsync(At(RedHolds[0]), true);
        Assert.False(Markup.IsRouteSelection);
        Assert.True(Markup.HasCandidate);
    }

    [Fact]
    public async Task The_timeline_shows_the_overlay_strip_of_the_laid_out_clip_and_follows_its_handles()
    {
        var clip = await ReviewOn(RedHolds[0]);
        await Markup.AcceptRouteCommand.ExecuteAsync(null);
        Markup.EscapeCommand.Execute(null);
        Assert.True(_edit.LayOutIntroAndFinal(clip.Id, T).Success);           // T = 150: orange [43, 150), green [0, 150)
        var template = _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<TemplateClip>().Single();
        Assert.True(_edit.SetClipProperties(template.Id,
            new ClipPropertyChange { Template = TemplateProperties.Of(template)!.Value with { Status = BoulderStatus.Top } }).Success);
        TimelineClipViewModel Vm() => _vm.Timeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Clip == clip);

        double X(long frame) => TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frame, Rate), _vm.Timeline.PixelsPerSecond);
        var strip = Vm().StripSegments;
        Assert.Equal(new[] { "#43A047", "#FB8C00", "#B39DDB" }, strip.Select(s => s.ColorHex));   // green, orange, lilac
        Assert.Equal((X(0), X(150)), (strip[0].Left, strip[0].Width));
        Assert.Equal((X(43), X(107)), (strip[1].Left, strip[1].Width));
        Assert.Equal((X(150), X(100)), (strip[2].Left, strip[2].Width));
        Assert.Contains("00:00:06:00", strip[2].Tip);

        Assert.True(_edit.EditOverlayStrip(clip.Id, OverlayStripHandle.Status, 25).Success);   // S → 175
        Assert.Equal((X(175), X(75)), (Vm().StripSegments[2].Left, Vm().StripSegments[2].Width));
        _undo.Undo();
        Assert.Equal(X(150), Vm().StripSegments[2].Left);
    }

    // --- helpers -----------------------------------------------------------------------------------------------------

    /// <summary>The markup started on <c>T</c> of the clip (the frame ready).</summary>
    private async Task<VideoClip> StartMarkup()
    {
        var clip = Select();
        Assert.True(Markup.StartCommand.CanExecute(null));
        await Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsActive);
        Assert.False(Markup.IsPreparing);
        Assert.NotNull(Markup.Geometry);
        return clip;
    }

    /// <summary>The markup started and its first click on <paramref name="control"/>: the analysis ran, the review is shown.</summary>
    private async Task<VideoClip> ReviewOn((int X, int Y, int R) control)
    {
        var clip = await StartMarkup();
        await Markup.OnClickAsync(At(control), false);
        Assert.True(Markup.IsRouteReview, _status.Message);
        Assert.True(Markup.IsRouteSelection);
        Assert.False(Markup.IsRouteAnalyzing);
        Assert.True(Markup.IsActive);
        return clip;
    }

    /// <summary>The markup frame: the wall with the route at the playhead's source time.</summary>
    private sealed class WallFrames : IHoldMarkupFrameSource
    {
        public Task<HoldMarkupFrame> GetAsync(VideoClip clip, MediaAsset asset, MediaTime playhead, FrameRate rate, CancellationToken cancellationToken) =>
            Task.FromResult(new HoldMarkupFrame(Wall, playhead - clip.TimelineStart));
    }

    /// <summary>The history count once the clip is on the timeline (adding it is a step of its own).</summary>
    private int _selected;

    /// <summary>A speck of the wall's noise: a pixel unlike its four neighbours (the stand-in model hides it).</summary>
    private static NormalizedPoint WallPoint()
    {
        var p = Wall.Pixels.ToArray();
        int Color(int x, int y) => p[(y * W + x) * 4] | p[(y * W + x) * 4 + 1] << 8 | p[(y * W + x) * 4 + 2] << 16;
        for (var y = H - 10; y > 10; y--)
        for (var x = 10; x < W - 10; x++)
        {
            var c = Color(x, y);
            if (c != Color(x - 1, y) && c != Color(x + 1, y) && c != Color(x, y - 1) && c != Color(x, y + 1))
                return new NormalizedPoint((x + 0.5) / W, (y + 0.5) / H);
        }
        throw new InvalidOperationException("No speck.");
    }

    private VideoClip Select()
    {
        if (_projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().FirstOrDefault() is { } existing)
        {
            _vm.Timeline.SetPlayhead(T);
            return existing;
        }
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(_root, "media", "climb.mp4"), Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(10), FrameRate = Rate, AvgFrameRate = Rate, Width = W, Height = H }
        };
        _projects.AddMediaAssets(new[] { asset });
        _vm.Timeline.AddMedia(asset);
        _vm.Timeline.SetPlayhead(T);
        _selected = _history;
        return _projects.Current.Timeline.VideoTracks.SelectMany(t => t.Clips).OfType<VideoClip>().Single();
    }

    private static NormalizedPoint At((int X, int Y, int R) disc) => new((disc.X + 0.5) / W, (disc.Y + 0.5) / H);

    private static bool Inside((int X, int Y, int R) disc, int x, int y) =>
        (x - disc.X) * (x - disc.X) + (y - disc.Y) * (y - disc.Y) <= disc.R * disc.R;

    private static bool Inside(ImmutableArray<NormalizedPoint> contour, (int X, int Y, int R) disc) =>
        contour.Average(p => p.X) * W is var cx && contour.Average(p => p.Y) * H is var cy &&
        Math.Abs(cx - disc.X) <= disc.R && Math.Abs(cy - disc.Y) <= disc.R;

    /// <summary>A textured grey wall (the tracking needs texture) with the route's red holds and one blue hold; the wall
    /// alone for another seed.</summary>
    private static DecodedFrame Scene(int seed)
    {
        var random = new Random(seed);
        var gray = new byte[W * H];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(170 + random.Next(10));
        for (var k = 0; k < 140; k++)
        {
            double cx = random.NextDouble() * W, cy = random.NextDouble() * H, r = 4 + random.NextDouble() * 10;
            var value = 40 + random.NextDouble() * 110;
            for (var y = Math.Max(0, (int)(cy - r)); y < Math.Min(H, (int)(cy + r) + 1); y++)
            for (var x = Math.Max(0, (int)(cx - r)); x < Math.Min(W, (int)(cx + r) + 1); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) gray[y * W + x] = (byte)Math.Clamp(value + (x - cx) * 2, 0, 255);
        }
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var v = gray[y * W + x];
            (byte R, byte G, byte B) c = (v, v, v);
            if (seed == 1)
            {
                foreach (var h in RedHolds) if (Inside(h, x, y)) c = Red;
                if (Inside(BlueHold, x, y)) c = Blue;
            }
            var i = (y * W + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
        }
        return new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    private static readonly DecodedFrame Wall = Scene(1);

    /// <summary>The model's stand-in: the mask of a prompt is the 4-connected region of its exact colour (the textured wall
    /// gives specks below the D034 contour's minimum, so a click there finds nothing).</summary>
    private sealed class FloodSegmentation : IHoldSegmentationService
    {
        public int Calls { get; private set; }

        /// <summary>The next prediction throws (a model failure during the analysis).</summary>
        public bool ThrowNext { get; set; }

        /// <summary>When set, every prediction waits for it (an analysis held open).</summary>
        public TaskCompletionSource? Gate { get; set; }

        public InferenceDevice Device => InferenceDevice.Gpu;
        public event EventHandler? DeviceChanged { add { } remove { } }

        public Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HoldModelStatus(HoldModelState.Ready, null));

        public Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromResult<IHoldFrame>(new Frame(this, frame));

        private sealed class Frame(FloodSegmentation owner, DecodedFrame frame) : IHoldFrame
        {
            private bool _disposed;

            public int Width => frame.Width;
            public int Height => frame.Height;

            public async Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
            {
                owner.Calls++;
                if (owner.Gate is { } gate) await gate.Task;
                if (owner.ThrowNext)
                {
                    owner.ThrowNext = false;
                    throw new InvalidOperationException("model failure");
                }
                ObjectDisposedException.ThrowIf(_disposed, this);              // as the real session once the mode is left
                int w = frame.Width, h = frame.Height;
                int sx = Math.Clamp((int)points[0].X, 0, w - 1), sy = Math.Clamp((int)points[0].Y, 0, h - 1);
                var px = frame.Pixels.ToArray();
                int Color(int x, int y) => px[(y * w + x) * 4] | px[(y * w + x) * 4 + 1] << 8 | px[(y * w + x) * 4 + 2] << 16;
                var seed = Color(sx, sy);
                var mask = new byte[w * h];
                var queue = new Queue<(int X, int Y)>();
                queue.Enqueue((sx, sy));
                mask[sy * w + sx] = 1;
                while (queue.Count > 0)
                {
                    var (x, y) = queue.Dequeue();
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || mask[ny * w + nx] != 0 || Color(nx, ny) != seed) continue;
                        mask[ny * w + nx] = 1;
                        queue.Enqueue((nx, ny));
                    }
                }
                var m = mask.Count(b => b != 0) < 20
                    ? new HoldMask(w, h, Enumerable.Repeat((byte)1, w * h).ToArray(), 0.9)   // a speck: the model sees the wall
                    : new HoldMask(w, h, mask, 0.9);
                return new HoldMaskSet(ImmutableArray.Create(m, m, m));
            }

            public void Dispose() => _disposed = true;
        }
    }
}
