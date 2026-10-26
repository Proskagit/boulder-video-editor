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
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 31 Step 31.4 (D047): automatic hold proposal review — ranked proposals, accept/reject, accept-all/reject-all,
/// automatic full-resolution refinement on accept, keyboard shortcuts, progress and error handling. The existing manual
/// D034 flow is preserved; proposal state is never persisted.
/// </summary>
public sealed class HoldMarkupProposalUiTests : IAsyncLifetime
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const int W = 200, H = 100;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TimelineEditService _edit;
    private readonly StatusService _status = new();
    private readonly HoldMarkupUiTests.FakeSegmentation _model = new();
    private readonly HoldMarkupUiTests.FakeFrames _frames = new();
    private readonly EditingLock _lock = new();
    private readonly FakeVideoDecoder _decoder = new();
    private readonly PlaybackService _playback;
    private readonly MainWindowViewModel _vm;
    private readonly FakeProposalGenerator _generator;
    private readonly FakeProposalRefiner _refiner;
    private int _history;

    public HoldMarkupProposalUiTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        _edit = new TimelineEditService(_projects, _undo, NullLogger<TimelineEditService>.Instance);
        _playback = new PlaybackService(_decoder, new FakeReferenceClock(), NullLogger<PlaybackService>.Instance,
            new PlaybackSettings { BufferFrames = 4 }, new FakeAudioDecoder(), new FakeAudioOutput());
        var analysis = new MediaAnalysisCoordinator(new HoldMarkupUiTests.NoAnalysis(), _projects, NullLogger<MediaAnalysisCoordinator>.Instance);
        var import = new MediaImportWorkflow(new ScriptedPicker(), new HoldMarkupUiTests.NoImport(), _projects, analysis, _status, NullLogger<MediaImportWorkflow>.Instance);
        var files = new ProjectFileWorkflow(_projects, analysis, new NullAutosave(), new ScriptedDialogs(), new ScriptedPicker(), _status, NullLogger<ProjectFileWorkflow>.Instance);

        _generator = new FakeProposalGenerator();
        _refiner = new FakeProposalRefiner();

        _vm = new MainWindowViewModel(
            new ToolbarViewModel(_undo, files, import, _status),
            new MediaBrowserViewModel(_projects, import, NullLogger<MediaBrowserViewModel>.Instance),
            new PreviewViewModel(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance),
            new InspectorViewModel(_edit, _status),
            new TimelineViewModel(_projects, _edit, _status, NullLogger<TimelineViewModel>.Instance),
            _status, files, _projects, NullLogger<MainWindowViewModel>.Instance,
            holdMarkup: new HoldMarkupViewModel(_edit, _projects, _status, _model, _frames, _lock, _generator, _refiner),
            previewTransform: new PreviewTransformViewModel(_edit, _projects, _status, _lock, _ => (10, 10)));
        _undo.StateChanged += (_, _) => _history++;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _playback.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HoldMarkupViewModel Markup => _vm.HoldMarkup!;
    private int UndoSteps() => _history;

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

    private async Task<VideoClip> StartWithProposals(int count)
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Proposals(count);
        _refiner.HoldFor = p => new Hold(p.Id, p.Contour, default, default, MediaTime.Zero);
        Assert.True(Markup.StartCommand.CanExecute(null));
        await Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsActive);
        Assert.False(Markup.IsGeneratingProposals);
        Assert.True(Markup.HasProposals);
        return clip;
    }

    private static IReadOnlyList<HoldProposal> Proposals(int count)
    {
        var proposals = new List<HoldProposal>(count);
        var centers = new[] { (0.3, 0.3), (0.7, 0.3), (0.3, 0.7), (0.7, 0.7), (0.5, 0.5) };
        for (var i = 0; i < count; i++)
        {
            var (x, y) = centers[i % centers.Length];
            var dx = i / centers.Length * 0.05;
            proposals.Add(Proposal(x + dx, y + dx, i + 1, 0.9 - i * 0.02));
        }
        return proposals;
    }

    private static HoldProposal Proposal(double x, double y, int rank, double score)
    {
        var r = 0.05;
        var contour = ImmutableArray.Create(
            new NormalizedPoint(x - r, y - r),
            new NormalizedPoint(x + r, y - r),
            new NormalizedPoint(x + r, y + r),
            new NormalizedPoint(x - r, y + r));
        return new HoldProposal(Guid.NewGuid(), contour, new NormalizedPoint(x, y), score, 0.05);
    }

    [Fact]
    public async Task Starting_markup_generates_ranked_proposals_and_shows_them()
    {
        var clip = await StartWithProposals(3);
        Assert.Equal(3, Markup.Proposals.Count);
        Assert.Equal(0, Markup.SelectedProposalIndex);
        Assert.Contains("1.", Markup.Proposals[0].Label);
        Assert.Contains("90", Markup.Proposals[0].Label);
        Assert.Equal(3, Markup.ProposalContours.Length);
        Assert.Equal(1, _generator.GenerateCalls);
    }

    [Fact]
    public async Task Accepting_a_single_proposal_refines_and_adds_one_hold_as_one_undo_step()
    {
        var clip = await StartWithProposals(2);
        var before = UndoSteps();

        await Markup.AcceptSelectedProposalCommand.ExecuteAsync(null);

        Assert.Single(clip.HoldMarkup!.Holds);
        Assert.Single(Markup.Proposals);
        Assert.Equal(1, _refiner.BatchCalls);
        Assert.Equal(before + 1, UndoSteps());
    }

    [Fact]
    public async Task Rejecting_a_proposal_discards_it_without_an_undo_step()
    {
        var clip = await StartWithProposals(3);
        var before = UndoSteps();

        Markup.RejectSelectedProposalCommand.Execute(null);

        Assert.Equal(2, Markup.Proposals.Count);
        Assert.Equal(before, UndoSteps());
    }

    [Fact]
    public async Task AcceptAll_adds_all_refined_holds_in_one_undo_step()
    {
        var clip = await StartWithProposals(3);
        var before = UndoSteps();

        await Markup.AcceptAllProposalsCommand.ExecuteAsync(null);

        Assert.Equal(3, clip.HoldMarkup!.Holds.Length);
        Assert.False(Markup.HasProposals);
        Assert.Equal(1, _refiner.BatchCalls);
        Assert.Equal(before + 1, UndoSteps());
    }

    [Fact]
    public async Task RejectAll_clears_proposals_without_an_undo_step()
    {
        var clip = await StartWithProposals(4);
        var before = UndoSteps();

        Markup.RejectAllProposalsCommand.Execute(null);

        Assert.False(Markup.HasProposals);
        Assert.Empty(Markup.Proposals);
        Assert.Equal(before, UndoSteps());
        Assert.Equal(before, UndoSteps());
    }

    [Fact]
    public async Task MaxProposalsToShow_limits_the_visible_list()
    {
        await StartWithProposals(12);
        Assert.Equal(12, Markup.Proposals.Count);
        Markup.MaxProposalsToShow = 10;
        Assert.Equal(10, Markup.Proposals.Count);
        Assert.Equal(10, Markup.ProposalContours.Length);
        Markup.MaxProposalsToShow = 20;
        Assert.Equal(12, Markup.Proposals.Count);
    }

    [Fact]
    public async Task Clicking_inside_a_proposal_contour_selects_it()
    {
        await StartWithProposals(2);
        var second = Markup.Proposals[1];
        var inside = second.Contour[0];   // a corner of the second proposal's square
        await Markup.OnClickAsync(inside, false);
        Assert.Equal(1, Markup.SelectedProposalIndex);
    }

    [Fact]
    public async Task Shortcuts_route_proposal_review_commands()
    {
        await StartWithProposals(2);
        Assert.Same(Markup.ConfirmOrAcceptCommand, ShortcutRouter.CommandFor(_vm, Key.Enter, KeyModifiers.None));
        Assert.Same(Markup.RejectOrDeleteCommand, ShortcutRouter.CommandFor(_vm, Key.Delete, KeyModifiers.None));
        Assert.Same(Markup.NextItemCommand, ShortcutRouter.CommandFor(_vm, Key.Tab, KeyModifiers.None));
        Assert.Same(Markup.PreviousItemCommand, ShortcutRouter.CommandFor(_vm, Key.Tab, KeyModifiers.Shift));
        Assert.Same(Markup.AcceptAllProposalsCommand, ShortcutRouter.CommandFor(_vm, Key.A, KeyModifiers.Control));
        Assert.Same(Markup.RejectAllProposalsCommand, ShortcutRouter.CommandFor(_vm, Key.D, KeyModifiers.Control));
    }

    [Fact]
    public async Task Tab_with_proposals_cycles_the_selection()
    {
        await StartWithProposals(3);
        Markup.NextItemCommand.Execute(null);
        Assert.Equal(1, Markup.SelectedProposalIndex);
        Markup.PreviousItemCommand.Execute(null);
        Assert.Equal(0, Markup.SelectedProposalIndex);
    }

    [Fact]
    public async Task Manual_click_after_rejecting_all_proposals_still_creates_a_hold()
    {
        var clip = await StartWithProposals(1);
        Markup.RejectAllProposalsCommand.Execute(null);
        Assert.False(Markup.HasProposals);

        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        await Markup.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(clip.HoldMarkup!.Holds);
    }

    [Fact]
    public async Task Refinement_failure_reports_and_keeps_the_proposal()
    {
        var clip = await StartWithProposals(2);
        _refiner.HoldFor = _ => null;   // every refinement fails
        await Markup.AcceptSelectedProposalCommand.ExecuteAsync(null);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(2, Markup.Proposals.Count);   // proposal not removed
    }

    [Fact]
    public async Task AcceptAll_removes_only_the_proposals_that_refinement_succeeded_for()
    {
        var clip = await StartWithProposals(3);
        var succeed = new[] { true, false, true };
        var i = 0;
        _refiner.HoldFor = _ => succeed[i++] ? new Hold(Guid.NewGuid(), Proposal(0.5, 0.5, 1, 0.9).Contour, default, default, MediaTime.Zero) : null;

        await Markup.AcceptAllProposalsCommand.ExecuteAsync(null);

        Assert.Equal(2, clip.HoldMarkup!.Holds.Length);
        Assert.Single(Markup.Proposals);   // the failed proposal remains
    }

    [Fact]
    public async Task Re_entering_a_clip_with_holds_skips_proposals()
    {
        var clip = AddVideo();
        Assert.True(_edit.AddHold(clip.Id, new Hold(Guid.NewGuid(), Proposal(0.5, 0.5, 1, 0.9).Contour, default, default, MediaTime.Zero)).Success);

        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Proposals(3);
        await Markup.StartCommand.ExecuteAsync(null);

        Assert.True(Markup.IsActive);
        Assert.False(Markup.HasProposals);
        Assert.Equal(0, _generator.GenerateCalls);
    }

    [Fact]
    public async Task Rejected_proposals_are_not_persisted()
    {
        var clip = await StartWithProposals(2);
        Markup.RejectAllProposalsCommand.Execute(null);
        Markup.DoneCommand.Execute(null);

        _generator.Proposals = Proposals(3);
        await Markup.StartCommand.ExecuteAsync(null);

        Assert.Equal(3, Markup.Proposals.Count);
        Assert.Null(clip.HoldMarkup);
    }

    [Fact]
    public async Task Cancel_during_generation_leaves_the_project_unchanged()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Proposals(3);
        _generator.Gate = new TaskCompletionSource();

        var start = Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsGeneratingProposals);
        Markup.DoneCommand.Execute(null);   // cancel and leave the mode
        _generator.Gate.SetResult();
        await start;

        Assert.Null(clip.HoldMarkup);
        Assert.False(Markup.HasProposals);
    }

    private sealed class FakeProposalGenerator : IHoldProposalGenerator
    {
        public IReadOnlyList<HoldProposal> Proposals { get; set; } = Array.Empty<HoldProposal>();
        public int GenerateCalls { get; private set; }
        public TaskCompletionSource? Gate { get; set; }

        public async Task<ProposalGenerationResult> GenerateAsync(VideoClip clip, MediaAsset asset, MediaTime playhead, FrameRate rate,
            ProposalPipelineOptions options, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            progress?.Report(0.5);
            if (Gate is { } gate) await gate.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new ProposalGenerationResult(Proposals, 16);
        }
    }

    private sealed class FakeProposalRefiner : IHoldProposalRefiner
    {
        public Func<HoldProposal, Hold?> HoldFor { get; set; } = p => new Hold(p.Id, p.Contour, default, default, MediaTime.Zero);
        public int BatchCalls { get; private set; }

        public Task<HoldProposalRefinementResult> RefineAsync(HoldProposal proposal, VideoClip clip, MediaAsset asset, MediaTime playhead,
            FrameRate rate, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HoldProposalRefinementResult(HoldFor(proposal), null));

        public Task<IReadOnlyList<HoldProposalRefinementResult>> RefineBatchAsync(IReadOnlyList<HoldProposal> proposals, VideoClip clip,
            MediaAsset asset, MediaTime playhead, FrameRate rate, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            progress?.Report(0.5);
            return Task.FromResult<IReadOnlyList<HoldProposalRefinementResult>>(proposals.Select(p => new HoldProposalRefinementResult(HoldFor(p), null)).ToList());
        }
    }

}
