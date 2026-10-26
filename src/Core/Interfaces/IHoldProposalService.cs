using AiVideoEditor.Core.Holds;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Automatic hold proposals (Phase 31, D047): generate ranked hold candidates on a frame without user clicks.
/// Implemented in Core; the ML backend is supplied through <see cref="IHoldFrame"/>.
/// </summary>
public interface IHoldProposalService
{
    /// <summary>
    /// Generate ranked hold proposals on <paramref name="frame"/>. The frame is already encoded; this method only runs
    /// the decoder at the grid points, filters masks, suppresses duplicates and ranks the survivors.
    /// </summary>
    Task<ProposalGenerationResult> GenerateProposalsAsync(
        IHoldFrame frame,
        ProposalPipelineOptions options,
        CancellationToken cancellationToken = default);
}
