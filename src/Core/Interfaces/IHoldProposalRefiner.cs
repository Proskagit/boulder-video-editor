using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Phase 31 refinement (D047): convert one accepted <see cref="HoldProposal"/> into a full-resolution
/// <see cref="Hold"/> by re-segmenting at the proposal's centroid on the source frame at its display size.
/// </summary>
public interface IHoldProposalRefiner
{
    /// <summary>
    /// Refines <paramref name="proposal"/> to a full-resolution <see cref="Hold"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="HoldProposalRefinementResult"/> with the hold on success, or a null hold and an explanatory
    /// message when refinement fails (no mask passes the candidate rule, the contour is invalid, the model is not
    /// ready, etc.). No incorrect hold is created.
    /// </returns>
    Task<HoldProposalRefinementResult> RefineAsync(
        HoldProposal proposal,
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refines several accepted proposals in one pass: the full-resolution frame is decoded and encoded once, then each
    /// proposal is re-prompted at its centroid. The results are returned in the same order as <paramref name="proposals"/>.
    /// </summary>
    Task<IReadOnlyList<HoldProposalRefinementResult>> RefineBatchAsync(
        IReadOnlyList<HoldProposal> proposals,
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of refining one accepted proposal (Phase 31, D047).</summary>
public sealed record HoldProposalRefinementResult(Hold? Hold, string? ErrorMessage)
{
    public bool IsSuccess => Hold is not null && ErrorMessage is null;
}
