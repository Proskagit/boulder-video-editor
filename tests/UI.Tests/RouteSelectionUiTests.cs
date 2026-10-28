using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.Timeline.Playback;
using AiVideoEditor.Timeline.Tests.Playback;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D048 stage 1 (§2, acceptance 1) through the real shell wiring with a stand-in model and synthetic frames: "Auto markup" (formerly "Place holds")
/// on <c>T</c> → the click on the control hold → the clean frame 5 s earlier in the Preview's overlay with the route's
/// holds proposed → the review (a rejected proposal stays rejected, the tolerance re-selects without the model, a missed
/// hold added by a click) → Accept = one <c>AddHolds</c> step, the holds at <c>t_C</c>, the playhead back to <c>T</c>;
/// Undo / Redo restore the clip exactly, a second run adds no duplicate; an unreliable transfer, frames that can't be
/// decoded and Cancel change nothing and return to <c>T</c>.
/// </summary>
public sealed class RouteSelectionUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const int W = 480, H = 270;
    private static readonly MediaTime T = MediaTime.FromSeconds(6);
    private static readonly MediaTime Clean = MediaTime.FromSeconds(1);

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
    private readonly SceneFrames _frames = new();
    private readonly MainWindowViewModel _vm;
    private readonly List<MediaTime> _seeks = new();
    private int _history;

    public RouteSelectionUiTests()
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
            holdMarkup: new HoldMarkupViewModel(_edit, _projects, _status, _model, new HoldMarkupUiTests.FakeFrames(),
                routeSelection: new RouteSelectionService(_frames)));
        var seek = Markup.SeekTo;
        Markup.SeekTo = t => { _seeks.Add(t); seek(t); };
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
    public async Task The_route_is_proposed_on_the_clean_frame_and_accepted_in_one_step_back_at_T()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Assert.Equal(_selected, _history);                                    // the review itself: no step
        Assert.True(Markup.ShowsMarkupFrame);
        Assert.Equal((W, H), (Markup.MarkupFrame!.Width, Markup.MarkupFrame.Height));
        Assert.Equal(new[] { T, Clean, Clean }, _frames.Requests);           // T and t_C for the transfer, t_C for the model
        Assert.Equal(RedHolds.Length, Markup.Proposals.Count);
        Assert.Null(clip.HoldMarkup);                                         // nothing yet
        Assert.False(Markup.AcceptAllProposalsCommand.CanExecute(null));       // D047's own accept: not in the route review

        var steps = _history;
        await Markup.AcceptRouteCommand.ExecuteAsync(null);

        Assert.Equal(steps + 1, _history);                                   // one AddHolds
        var holds = clip.HoldMarkup!.Holds;
        Assert.Equal(RedHolds.Length, holds.Length);
        Assert.All(holds, h => Assert.Equal(Clean, h.SourceTime));          // marked at t_C
        Assert.All(RedHolds, r => Assert.Single(holds, h => Inside(h.Points, r)));
        Assert.False(Markup.IsActive);
        Assert.False(Markup.ShowsMarkupFrame);
        Assert.Equal(new[] { T }, _seeks);
        Assert.Equal(T, _vm.Timeline.Playhead);
    }

    [Fact]
    public async Task Undo_restores_the_clip_whole_redo_repeats_it_and_a_second_run_adds_no_duplicate()
    {
        var clip = await ReviewOn(RedHolds[0]);
        await Markup.AcceptRouteCommand.ExecuteAsync(null);
        var added = clip.HoldMarkup!;

        _undo.Undo();
        Assert.Null(clip.HoldMarkup);
        _undo.Redo();
        Assert.Equal(added.Holds.Select(h => h.Id), clip.HoldMarkup!.Holds.Select(h => h.Id));

        var steps = _history;
        await ReviewOn(RedHolds[1], select: false);
        Assert.Empty(Markup.Proposals);
        Assert.Equal(Messages.RouteSelectionNothingNew, _status.Message);
        Assert.False(Markup.AcceptRouteCommand.CanExecute(null));
        Markup.CancelRouteCommand.Execute(null);
        Assert.Equal(steps, _history);
        Assert.Equal(RedHolds.Length, clip.HoldMarkup.Holds.Length);
        Assert.Equal(T, _vm.Timeline.Playhead);
    }

    [Fact]
    public async Task The_review_rejects_widens_and_adds_by_click_before_the_one_step()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Markup.SelectedProposalIndex = 1;
        var rejected = Markup.SelectedProposal!.Proposal.Id;
        Markup.RejectSelectedProposalCommand.Execute(null);
        Assert.Equal(RedHolds.Length - 1, Markup.Proposals.Count);
        Markup.RouteTolerance = 0.2m;                                       // re-selects; the rejection stays
        Assert.DoesNotContain(Markup.Proposals, p => p.Proposal.Id == rejected);
        var calls = _model.Calls;
        Markup.RouteTolerance = 0.05m;
        Assert.Equal(calls, _model.Calls);                                   // no model call

        await Markup.OnClickAsync(At(BlueHold), false);                      // a missed hold: a candidate, Enter adds it
        Assert.True(Markup.HasCandidate);
        await Markup.ConfirmCommand.ExecuteAsync(null);
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
    public async Task An_unreliable_transfer_changes_nothing_and_returns_to_T()
    {
        _frames.Differ = true;                                                // the camera "moved" beyond recognition
        await Failed(Messages.RouteSelectionTransferFailed);
    }

    [Fact]
    public async Task Frames_that_cant_be_decoded_change_nothing_and_return_to_T()
    {
        _frames.Throws = true;
        await Failed(string.Format(Messages.RouteSelectionFrameUnavailable, "no decoder"));
    }

    [Fact]
    public async Task A_click_beside_any_hold_on_the_clean_frame_changes_nothing()
    {
        var clip = Select();
        Markup.PlaceHoldsCommand.Execute(null);
        await Markup.OnClickAsync(WallPoint(), false);                        // the wall
        Assert.Equal(Messages.RouteSelectionNoControlHold, _status.Message);
        Assert.False(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
        Assert.Equal(new[] { T }, _seeks);
    }

    [Fact]
    public async Task Cancel_in_the_review_changes_nothing_and_returns_to_T()
    {
        var clip = await ReviewOn(RedHolds[0]);
        Markup.EscapeCommand.Execute(null);                                   // Esc: nothing selected, no candidate → cancel
        Assert.False(Markup.IsActive);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
        Assert.Equal(new[] { T }, _seeks);
        Assert.Equal(T, _vm.Timeline.Playhead);
    }

    [Fact]
    public void The_button_needs_a_paused_playhead_on_the_selected_clip()
    {
        Assert.False(Markup.PlaceHoldsCommand.CanExecute(null));             // no clip
        Select();
        Assert.True(Markup.PlaceHoldsCommand.CanExecute(null));
        _vm.Timeline.SetPlayhead(MediaTime.FromSeconds(10));                  // T = Ve: no frame of the clip
        Assert.False(Markup.PlaceHoldsCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_timeline_shows_the_overlay_strip_of_the_laid_out_clip_and_follows_its_handles()
    {
        var clip = await ReviewOn(RedHolds[0]);
        await Markup.AcceptRouteCommand.ExecuteAsync(null);
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

    private async Task Failed(string message)
    {
        var clip = Select();
        Markup.PlaceHoldsCommand.Execute(null);
        await Markup.OnClickAsync(At(RedHolds[0]), false);
        Assert.Equal(message, _status.Message);
        Assert.False(Markup.IsActive);
        Assert.False(Markup.ShowsMarkupFrame);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(_selected, _history);
        Assert.Equal(new[] { T }, _seeks);
        Assert.Equal(T, _vm.Timeline.Playhead);
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

    private async Task<VideoClip> ReviewOn((int X, int Y, int R) control, bool select = true)
    {
        var clip = Select();
        Assert.True(Markup.PlaceHoldsCommand.CanExecute(null));
        Markup.PlaceHoldsCommand.Execute(null);
        Assert.True(Markup.IsActive);
        Assert.True(Markup.IsRouteSelection);
        Assert.False(Markup.IsRouteReview);
        Assert.NotNull(Markup.Geometry);                                     // the click at T can be mapped
        await Markup.OnClickAsync(At(control), false);
        Assert.True(Markup.IsRouteReview, _status.Message);
        Assert.False(Markup.IsPreparing);
        return clip;
    }

    private static NormalizedPoint At((int X, int Y, int R) disc) => new((disc.X + 0.5) / W, (disc.Y + 0.5) / H);

    private static bool Inside((int X, int Y, int R) disc, int x, int y) =>
        (x - disc.X) * (x - disc.X) + (y - disc.Y) * (y - disc.Y) <= disc.R * disc.R;

    private static bool Inside(ImmutableArray<NormalizedPoint> contour, (int X, int Y, int R) disc) =>
        contour.Average(p => p.X) * W is var cx && contour.Average(p => p.Y) * H is var cy &&
        Math.Abs(cx - disc.X) <= disc.R && Math.Abs(cy - disc.Y) <= disc.R;

    /// <summary>A textured grey wall (the tracking needs texture) with the route's red holds and one blue hold; another
    /// seed for a frame the camera "moved" from beyond recognition.</summary>
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
    private static readonly DecodedFrame Elsewhere = Scene(2);

    /// <summary>The frames by source time: the same wall at <c>T</c> and on the clean frame (a still camera), unless
    /// <see cref="Differ"/>; <see cref="Throws"/>: a decoder failure.</summary>
    private sealed class SceneFrames : IRouteSelectionFrameSource
    {
        public bool Differ { get; set; }
        public bool Throws { get; set; }
        public List<MediaTime> Requests { get; } = new();

        public Task<RouteSelectionFrame> DecodeAsync(MediaAsset asset, MediaTime sourceTime, int maxWidth, int maxHeight, CancellationToken cancellationToken)
        {
            if (Throws) throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "no decoder");
            Requests.Add(sourceTime);
            return Task.FromResult(new RouteSelectionFrame(Differ && sourceTime == T ? Elsewhere : Wall, sourceTime));
        }
    }

    /// <summary>The model's stand-in: the mask of a prompt is the 4-connected region of its exact colour (the textured wall
    /// gives specks below the D034 contour's minimum, so a click there finds nothing).</summary>
    private sealed class FloodSegmentation : IHoldSegmentationService
    {
        public int Calls { get; private set; }
        public InferenceDevice Device => InferenceDevice.Gpu;
        public event EventHandler? DeviceChanged { add { } remove { } }

        public Task<HoldModelStatus> EnsureReadyAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HoldModelStatus(HoldModelState.Ready, null));

        public Task<IHoldFrame> PrepareFrameAsync(DecodedFrame frame, CancellationToken cancellationToken = default) =>
            Task.FromResult<IHoldFrame>(new Frame(this, frame));

        private sealed class Frame(FloodSegmentation owner, DecodedFrame frame) : IHoldFrame
        {
            public int Width => frame.Width;
            public int Height => frame.Height;

            public Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
            {
                owner.Calls++;
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
                return Task.FromResult(new HoldMaskSet(ImmutableArray.Create(m, m, m)));
            }

            public void Dispose() { }
        }
    }
}
