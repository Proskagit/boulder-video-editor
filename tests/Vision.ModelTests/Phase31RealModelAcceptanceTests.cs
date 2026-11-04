using System.ComponentModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;
using Xunit;
using Xunit.Abstractions;

namespace AiVideoEditor.Vision.ModelTests;

/// <summary>
/// Phase 31's manual plan, parts A, B and E, on the real model and real footage (the pinned SAM 2.1 Large ONNX and the E1
/// 4K clip of the owner's gym): the proposal pass with its progress, the ranked top-N, accept (a full-resolution refined
/// hold with its colours), reject with the next selected, accept-all and reject-all, a cancel while it runs, a Shift+click
/// candidate over a proposal confirmed with Enter, Esc, a seek leaving the mode, re-entering, and the accepted holds as the
/// outlines the Preview and the export draw. Local only (the model and the clip are not in the repository).
/// </summary>
public sealed class Phase31RealModelAcceptanceTests(ITestOutputHelper output)
{
    private static string Clip4K => ReferenceData.Path_("clips/12x10/1014(6).mp4");

    private static async Task<(Core.Common.UndoRedoService Undo, Project.ProjectService Projects, UI.ViewModels.Panels.HoldMarkupViewModel Markup, VideoClip Clip)>
        StartAsync()
    {
        var s = Phase31ManualSurrogateTests.Setup(Clip4K);
        Assert.True((await s.Segmentation.EnsureReadyAsync()).IsReady);
        return (s.Undo, s.Projects, s.Markup, s.Clip);
    }

    [Fact]
    public async Task A1_A3_A5_A6_A7_A8_the_real_pass_its_ranked_top_30_accept_reject_and_the_batch_operations()
    {
        var (undo, _, markup, clip) = await StartAsync();
        var states = new List<(bool Generating, double Progress)>();
        ((INotifyPropertyChanged)markup).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(markup.IsGeneratingProposals) or nameof(markup.ProposalProgress))
                states.Add((markup.IsGeneratingProposals, markup.ProposalProgress));
        };

        await markup.StartCommand.ExecuteAsync(null);

        // A1: the progress ran and proposals are drawn.
        Assert.Contains(states, st => st.Generating);
        Assert.Contains(states, st => st.Progress > 0 && st.Progress < 1);
        Assert.False(markup.IsGeneratingProposals);
        Assert.True(markup.HasProposals);
        Assert.Equal(markup.Proposals.Count, markup.ProposalContours.Length);
        // A3: at most 30 by default, ranked by score, each with its rank and score.
        Assert.InRange(markup.Proposals.Count, 1, 30);
        Assert.True(markup.Proposals.Zip(markup.Proposals.Skip(1)).All(p => p.First.Score >= p.Second.Score));
        Assert.All(markup.Proposals.Select((p, i) => (p, i)), x => Assert.Equal(x.i + 1, x.p.Rank));
        output.WriteLine($"proposals: {markup.Proposals.Count}; top scores {string.Join(", ", markup.Proposals.Take(5).Select(p => p.Score.ToString("F3")))}");

        // A5: accepting the selected one refines it at full resolution into a confirmed hold with its colours.
        var count = markup.Proposals.Count;
        var accepted = markup.SelectedProposal!.Proposal;
        await markup.AcceptSelectedProposalCommand.ExecuteAsync(null);
        var hold = Assert.Single(clip.HoldMarkup!.Holds);
        Assert.True(hold.Points.Length >= 3);
        Assert.NotEqual(default, hold.SurfaceLab);
        // The top-N window is refilled from the remaining proposals: the accepted one is gone, the list stays ≤ 30.
        Assert.DoesNotContain(markup.Proposals, p => p.Proposal.Id == accepted.Id);
        Assert.InRange(markup.Proposals.Count, count - 1, 30);

        // A6: rejecting removes the selected one and selects the next.
        var index = markup.SelectedProposalIndex;
        var rejected = markup.SelectedProposal!.Proposal.Id;
        var next = markup.Proposals[index + 1].Proposal.Id;
        markup.RejectSelectedProposalCommand.Execute(null);
        Assert.DoesNotContain(markup.Proposals, p => p.Proposal.Id == rejected);
        Assert.Equal(next, markup.SelectedProposal!.Proposal.Id);

        // A7: accept-all confirms every shown proposal in one Undo step.
        var shown = markup.Proposals.Select(p => p.Proposal.Id).ToHashSet();
        await markup.AcceptAllProposalsCommand.ExecuteAsync(null);
        Assert.Equal(1 + shown.Count, clip.HoldMarkup!.Holds.Length);
        // Only the shown ones were accepted (D047, the fix of Step 31.6); the window now shows the next of the rest, if any.
        Assert.DoesNotContain(markup.Proposals, p => shown.Contains(p.Proposal.Id));
        Assert.InRange(markup.Proposals.Count, 0, 30);
        undo.Undo();
        Assert.Single(clip.HoldMarkup!.Holds);

        // A8: on a new pass (the first accept undone too: no holds, so proposals are offered) reject-all clears them and the
        // manual flow stays.
        markup.DoneCommand.Execute(null);
        undo.Undo();
        Assert.Null(clip.HoldMarkup);
        await markup.StartCommand.ExecuteAsync(null);
        Assert.True(markup.HasProposals);
        markup.RejectAllProposalsCommand.Execute(null);
        Assert.False(markup.HasProposals);
        Assert.True(markup.IsActive);
        await markup.OnClickAsync(new NormalizedPoint(0.5, 0.5), false);
        Assert.True(markup.HasCandidate);
    }

    [Fact]
    public async Task A10_cancelling_the_real_pass_shows_nothing_and_changes_nothing()
    {
        var (undo, projects, markup, clip) = await StartAsync();
        var history = undo.CurrentPosition;                                     // the clip added by the setup
        var dirty = projects.Current.IsDirty;
        var start = markup.StartCommand.ExecuteAsync(null);
        await Task.Delay(300);                                                  // the pass is running
        Assert.True(markup.IsGeneratingProposals || markup.IsActive);
        markup.EscapeCommand.Execute(null);                                     // cancel
        await start;

        Assert.False(markup.IsGeneratingProposals);
        Assert.False(markup.HasProposals);
        Assert.Null(clip.HoldMarkup);
        Assert.Same(history, undo.CurrentPosition);                             // no Undo step
        Assert.Equal(dirty, projects.Current.IsDirty);
    }

    [Fact]
    public async Task B1_B2_B3_B4_B5_a_real_manual_candidate_inside_the_review_Esc_Enter_a_seek_and_re_entering()
    {
        var (_, _, markup, clip) = await StartAsync();
        await markup.StartCommand.ExecuteAsync(null);
        var count = markup.Proposals.Count;
        var over = markup.Proposals[Math.Min(1, count - 1)].Proposal.Centroid;   // the proposal's centre

        // B1: Shift+click over a proposal: a real candidate from the model at that point; the review stays.
        await markup.OnClickAsync(over, additive: true);
        Assert.True(markup.HasCandidate);
        Assert.Equal(count, markup.Proposals.Count);

        // B2: Esc discards the candidate; the review is back; a second Esc changes nothing.
        markup.EscapeCommand.Execute(null);
        Assert.False(markup.HasCandidate);
        Assert.Equal(count, markup.Proposals.Count);
        markup.EscapeCommand.Execute(null);
        Assert.True(markup.IsActive);
        Assert.Equal(count, markup.Proposals.Count);

        // B3: a manual candidate confirmed with Enter; the unreviewed proposals stay.
        await markup.OnClickAsync(over, additive: true);
        await markup.ConfirmOrAcceptCommand.ExecuteAsync(null);
        Assert.Single(clip.HoldMarkup!.Holds);
        Assert.Equal(count, markup.Proposals.Count);

        // B4: a seek leaves the mode; the manual hold stays, the unreviewed proposals are gone.
        markup.Playhead = () => MediaTime.FromSeconds(1.5);
        markup.OnPlayheadMoved();
        Assert.False(markup.IsActive);
        Assert.False(markup.HasProposals);
        Assert.Single(clip.HoldMarkup!.Holds);

        // B5: re-entering the clip that has holds runs no proposal pass and shows them.
        markup.Playhead = () => MediaTime.FromSeconds(0.5);
        await markup.StartCommand.ExecuteAsync(null);
        Assert.True(markup.IsActive);
        Assert.False(markup.HasProposals);
        Assert.Equal(1, markup.HoldCount);
    }

    [Fact]
    public async Task E1_the_accepted_real_holds_are_exactly_the_outlines_the_preview_and_the_export_draw()
    {
        var (_, projects, markup, clip) = await StartAsync();
        await markup.StartCommand.ExecuteAsync(null);
        await markup.AcceptAllProposalsCommand.ExecuteAsync(null);
        var holds = clip.HoldMarkup!.Holds;
        Assert.NotEmpty(holds);

        // The one snapshot both the Preview's pipeline and the export render (D023): its outline is the accepted holds.
        var snapshot = PlaybackSnapshotBuilder.Build(projects.Current, 1);
        var outline = snapshot.VideoLayers.SelectMany(l => l.Spans).Single(s => s.ClipId == clip.Id).Outline!;
        Assert.Equal(holds.Select(h => h.Points), outline.Polygons);
        Assert.Equal(clip.HoldMarkup.ColorHex, outline.ColorHex);
        var layer = snapshot.LayersAt(MediaTime.FromSeconds(0.5)).OfType<Core.Composition.PictureLayer>().Single();
        Assert.Same(outline, layer.Span.Outline);
    }
}
