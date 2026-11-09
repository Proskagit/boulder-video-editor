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
using AiVideoEditor.UI.Localization;
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
            // D057 §3.15: proposals are off by default — these tests are of the proposals, so the user turned them on.
            holdMarkup: new HoldMarkupViewModel(_edit, _projects, _status, _model, _frames, _lock, _generator, _refiner,
                settings: new InMemoryUserSettings(new UserSettings { HoldProposalsEnabled = true })),
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

    // --- Phase 31 manual plan A / B / E as automated acceptance (2026-11-04) ----------------------------------------------

    [Fact]
    public async Task A1a_with_the_toggle_off_no_proposal_pass_runs_and_the_manual_flow_is_there_at_once()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Proposals(3);
        Markup.IsProposalGenerationEnabled = false;

        await Markup.StartCommand.ExecuteAsync(null);

        Assert.True(Markup.IsActive);
        Assert.Equal(0, _generator.GenerateCalls);
        Assert.False(Markup.HasProposals);
        Assert.Equal(HoldMarkupViewModel.ReadyText, Markup.ModeText);
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        Assert.True(Markup.HasCandidate);
    }

    [Fact]
    public async Task A2_proposals_are_their_own_layer_apart_from_confirmed_holds_and_the_selected_one_is_marked()
    {
        var clip = await StartWithProposals(3);
        Assert.Equal(Markup.Proposals.Select(p => p.Contour), Markup.ProposalContours);
        Assert.Null(clip.HoldMarkup);                                         // nothing confirmed yet
        Markup.NextItemCommand.Execute(null);
        Assert.Equal(1, Markup.SelectedProposalIndex);                        // the highlighted one

        await Markup.AcceptSelectedProposalCommand.ExecuteAsync(null);
        var hold = Assert.Single(clip.HoldMarkup!.Holds);
        Assert.Equal(2, Markup.ProposalContours.Length);
        Assert.DoesNotContain(hold.Points, Markup.ProposalContours);          // the accepted contour left the proposal layer
    }

    [Fact]
    public async Task A3_at_most_thirty_proposals_are_shown_by_default_each_with_its_rank_and_score()
    {
        await StartWithProposals(40);
        Assert.Equal(30, Markup.MaxProposalsToShow);
        Assert.Equal(30, Markup.Proposals.Count);
        for (var i = 0; i < Markup.Proposals.Count; i++)
        {
            var item = Markup.Proposals[i];
            Assert.Equal(i + 1, item.Rank);
            Assert.Equal(string.Format(Strings.Holds_ProposalRank, i + 1, item.Score), item.Label);
        }
        Assert.True(Markup.Proposals.Zip(Markup.Proposals.Skip(1)).All(p => p.First.Score >= p.Second.Score));   // ranked
    }

    [Fact]
    public async Task A6_rejecting_selects_the_next_proposal()
    {
        await StartWithProposals(3);
        var next = Markup.Proposals[1].Proposal.Id;
        Markup.RejectOrDeleteCommand.Execute(null);                            // Delete
        Assert.Equal(2, Markup.Proposals.Count);
        Assert.Equal(next, Markup.SelectedProposal!.Proposal.Id);
    }

    [Fact]
    public async Task A9_no_proposals_say_so_and_leave_the_manual_flow()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Array.Empty<HoldProposal>();

        await Markup.StartCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Holds_NoProposals, _status.Message);
        Assert.True(Markup.IsActive);
        Assert.False(Markup.HasProposals);
        Assert.Equal(HoldMarkupViewModel.ReadyText, Markup.ModeText);
        await Markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        await Markup.ConfirmCommand.ExecuteAsync(null);
        Assert.Single(clip.HoldMarkup!.Holds);
    }

    [Fact]
    public async Task A10_cancelling_the_generation_stops_the_progress_and_changes_nothing()
    {
        var clip = AddVideo();
        _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(1));
        _generator.Proposals = Proposals(3);
        _generator.Gate = new TaskCompletionSource();
        var before = UndoSteps();

        var start = Markup.StartCommand.ExecuteAsync(null);
        Assert.True(Markup.IsGeneratingProposals);
        Assert.Equal(0.5, Markup.ProposalProgress);
        Markup.EscapeCommand.Execute(null);                                    // Esc while it runs: cancel and leave
        _generator.Gate.SetResult();
        await start;

        Assert.False(Markup.IsGeneratingProposals);
        Assert.False(Markup.IsActive);
        Assert.False(Markup.HasProposals);
        Assert.Null(clip.HoldMarkup);
        Assert.Equal(before, UndoSteps());
    }

    [Fact]
    public async Task B1_shift_click_starts_a_manual_candidate_even_over_a_proposal_and_the_review_stays()
    {
        await StartWithProposals(3);
        var inside = Markup.Proposals[1].Contour[0];

        await Markup.OnClickAsync(inside, additive: true);                    // Shift+click over the second proposal

        Assert.True(Markup.HasCandidate);
        Assert.Equal(0, Markup.SelectedProposalIndex);                         // the selection is not moved
        Assert.Equal(3, Markup.Proposals.Count);                               // the proposals stay visible
        Assert.Equal(3, Markup.ProposalContours.Length);
    }

    [Fact]
    public async Task B2_Esc_discards_the_manual_candidate_and_returns_to_the_review_which_a_second_Esc_leaves_unchanged()
    {
        await StartWithProposals(3);
        await Markup.OnClickAsync(new NormalizedPoint(0.95, 0.95), additive: true);
        Assert.True(Markup.HasCandidate);

        Assert.Same(Markup.EscapeCommand, ShortcutRouter.CommandFor(_vm, Key.Escape, KeyModifiers.None));
        Markup.EscapeCommand.Execute(null);
        Assert.False(Markup.HasCandidate);
        Assert.True(Markup.IsActive);
        Assert.Equal(3, Markup.Proposals.Count);

        Markup.EscapeCommand.Execute(null);                                    // no candidate: the review unchanged (D047 §2)
        Assert.True(Markup.IsActive);
        Assert.Equal(3, Markup.Proposals.Count);
    }

    [Fact]
    public async Task B3_a_manual_hold_confirmed_with_Enter_during_the_review_keeps_the_unreviewed_proposals()
    {
        var clip = await StartWithProposals(3);
        await Markup.OnClickAsync(new NormalizedPoint(0.95, 0.95), additive: true);
        var before = UndoSteps();

        Assert.Same(Markup.ConfirmOrAcceptCommand, ShortcutRouter.CommandFor(_vm, Key.Enter, KeyModifiers.None));
        await Markup.ConfirmOrAcceptCommand.ExecuteAsync(null);                 // Enter with the manual candidate

        var hold = Assert.Single(clip.HoldMarkup!.Holds);
        Assert.DoesNotContain(hold.Points, Markup.Proposals.Select(p => p.Contour));   // the manual hold, not a proposal
        Assert.Equal(3, Markup.Proposals.Count);                               // all three still to review
        Assert.False(Markup.HasCandidate);
        Assert.True(UndoSteps() > before);
    }

    public static TheoryData<string> Leaves => new() { "Done", "Play", "Seek" };

    [Theory]
    [MemberData(nameof(Leaves))]
    public async Task B4_Done_Play_or_Seek_leaves_the_mode_keeping_the_accepted_and_manual_holds_only(string how)
    {
        var clip = await StartWithProposals(4);
        await Markup.AcceptSelectedProposalCommand.ExecuteAsync(null);         // one accepted
        Markup.RejectSelectedProposalCommand.Execute(null);                    // one rejected
        await Markup.OnClickAsync(new NormalizedPoint(0.95, 0.95), additive: true);
        await Markup.ConfirmCommand.ExecuteAsync(null);                         // one manual; two unreviewed remain
        Assert.Equal(2, Markup.Proposals.Count);

        switch (how)
        {
            case "Done": Markup.DoneCommand.Execute(null); break;
            case "Play": _vm.Preview.IsPlaying = true; break;
            default: _vm.Timeline.SetPlayhead(clip.TimelineStart + MediaTime.FromSeconds(2)); break;
        }

        Assert.False(Markup.IsActive);
        Assert.False(Markup.HasProposals);
        Assert.Empty(Markup.ProposalContours);
        Assert.Equal(2, clip.HoldMarkup!.Holds.Length);                         // the accepted and the manual
    }

    [Fact]
    public async Task B5_re_entering_a_clip_with_holds_offers_no_proposals_and_shows_its_holds()
    {
        var clip = await StartWithProposals(2);
        await Markup.AcceptAllProposalsCommand.ExecuteAsync(null);
        Markup.DoneCommand.Execute(null);
        var calls = _generator.GenerateCalls;

        await Markup.StartCommand.ExecuteAsync(null);

        Assert.True(Markup.IsActive);
        Assert.False(Markup.HasProposals);
        Assert.Equal(calls, _generator.GenerateCalls);
        Assert.Equal(2, Markup.HoldCount);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void E2_the_review_texts_follow_the_interface_language(string culture)
    {
        // Read per culture without touching the shared Strings.Culture: the UI tests run in parallel.
        var info = System.Globalization.CultureInfo.GetCultureInfo(culture);
        string Text(string name) => Strings.ResourceManager.GetString(name, info)!;
        var texts = new[] { Text(nameof(Strings.Holds_ProposeAutomatically)), Text(nameof(Strings.Holds_Accept)), Text(nameof(Strings.Holds_Reject)),
            Text(nameof(Strings.Holds_AcceptAll)), Text(nameof(Strings.Holds_RejectAll)), Text(nameof(Strings.Holds_NoProposals)),
            Text(nameof(Strings.Holds_GeneratingProposals)), Text(nameof(Strings.Holds_ReviewProposals)), Text(nameof(Strings.Holds_ProposalsHeading)),
            string.Format(info, Text(nameof(Strings.Holds_ProposalRank)), 1, 0.9) };
        if (culture == "ru")
        {
            Assert.Equal(new[] { "Предлагать зацепки автоматически", "Принять", "Отклонить", "Принять все", "Отклонить все",
                "Зацепки не найдены — разметьте вручную." }, texts.Take(6));
            Assert.All(texts, t => Assert.Matches("[А-Яа-яЁё]", t));          // every review text in Russian
        }
        else
        {
            Assert.All(texts, t => Assert.DoesNotMatch("[А-Яа-яЁё]", t));     // and in English in the English interface
        }
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
