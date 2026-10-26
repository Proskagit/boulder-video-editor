using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>
/// Phase 31 integration: decode one markup frame at the proposal pass resolution, run the hold segmentation model on it
/// and return ranked automatic hold proposals. The returned proposals use normalized source coordinates that match the
/// existing D034 hold contour space.
/// </summary>
public interface IHoldProposalGenerator
{
    /// <summary>
    /// Generates hold proposals for the frame of <paramref name="clip"/> under <paramref name="playhead"/>.
    /// </summary>
    /// <param name="clip">The video clip to mark.</param>
    /// <param name="asset">The clip's media asset.</param>
    /// <param name="playhead">Timeline playhead position.</param>
    /// <param name="rate">Timeline frame rate.</param>
    /// <param name="options">Proposal pipeline parameters.</param>
    /// <param name="progress">Reports coarse progress: decode, model preparation, proposal generation.</param>
    /// <param name="cancellationToken">Cancellation is checked between phases and inside the grid loop.</param>
    /// <exception cref="VideoDecodeException">The frame could not be decoded.</exception>
    /// <exception cref="InvalidOperationException">The model is not ready or the asset has no metadata.</exception>
    Task<ProposalGenerationResult> GenerateAsync(
        VideoClip clip,
        MediaAsset asset,
        MediaTime playhead,
        FrameRate rate,
        ProposalPipelineOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
