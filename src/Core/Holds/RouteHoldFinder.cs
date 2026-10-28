using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;

namespace AiVideoEditor.Core.Holds;

/// <summary>The prompts of a route search (D048 §2): the colour map (always) and, by default, the D047 grid as well — on
/// T1 the two together found the most route holds (recall 0.83 / 0.93 against 0.78 / 0.87 with colour alone).</summary>
public sealed record RouteHoldSearchOptions
{
    public double MapThreshold { get; init; } = ColorPromptPlanner.MapThreshold;
    public int MaxColorPrompts { get; init; } = ColorPromptPlanner.MaxPrompts;
    public bool UseGrid { get; init; } = true;
    public int GridDivisions { get; init; } = new ProposalPipelineOptions().GridDivisions;
    public double NmsIouThreshold { get; init; } = new ProposalPipelineOptions().NmsIouThreshold;
}

/// <summary>Every candidate of one clean frame, the reference colour and the control hold — selection by tolerance is
/// <see cref="RouteHoldSelector"/>'s, without the model.</summary>
public sealed record RouteHoldSearch(OkLabColor Reference, IReadOnlyList<RouteHoldCandidate> Candidates, int Prompts)
{
    public IReadOnlyList<RouteHoldCandidate> Select(double tolerance, IReadOnlySet<Guid>? rejected = null) =>
        RouteHoldSelector.Select(Candidates, tolerance, rejected);
}

/// <summary>A search, or why there is none (nothing is created either way).</summary>
public readonly record struct RouteHoldSearchResult(RouteHoldSearch? Search, string? Error);

/// <summary>
/// D048 §2 colour classification on the clean frame (<c>PHASE_HOLD_TRACKING.md</c> S1–S2), on the existing D034 / D047
/// pieces: the control hold's mask by the D034 candidate rule (<see cref="HoldCandidateRules"/>, its largest component) at the
/// transferred point → the reference colour by its area (<see cref="HoldColorSampler"/>'s surface) → prompts from the
/// colour map (<see cref="ColorPromptPlanner"/>) plus the D047 grid (<see cref="HoldProposalService.GridPoints"/>) → one mask
/// per prompt by the same rule, its surface colour and distance → duplicates (mask IoU ≥ the D047 NMS threshold) merged,
/// the higher score kept. A prompt inside a mask already found is skipped (the same hold again). Candidates on a hold the
/// clip already has (its centre inside an existing contour) are left out, so a second run adds no duplicates.
/// </summary>
public static class RouteHoldFinder
{
    public static async Task<RouteHoldSearchResult> FindAsync(IHoldFrame encoded, DecodedFrame frame, NormalizedPoint control,
        IReadOnlyList<ImmutableArray<NormalizedPoint>> existingHolds, RouteHoldSearchOptions options,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(frame);
        if (encoded.Width != frame.Width || encoded.Height != frame.Height)
            throw new ArgumentException("The encoded frame doesn't match the decoded one.", nameof(encoded));
        int w = frame.Width, h = frame.Height;

        var controlPrompt = new PointD(Math.Clamp(control.X, 0, 1) * w, Math.Clamp(control.Y, 0, 1) * h);
        var controlMask = await MaskAt(encoded, controlPrompt, w, h, cancellationToken).ConfigureAwait(false);
        if (controlMask is null) return new RouteHoldSearchResult(null, Messages.RouteSelectionNoControlHold);
        var (controlCrop, controlScore, controlContour) = controlMask.Value;
        var reference = Surface(frame, controlCrop);

        var prompts = ColorPromptPlanner.Plan(frame, reference, options.MapThreshold, maxPrompts: options.MaxColorPrompts).ToList();
        if (options.UseGrid) prompts.AddRange(HoldProposalService.GridPoints(w, h, options.GridDivisions));

        var candidates = new List<RouteHoldCandidate>
        {
            Candidate(controlCrop, controlScore, controlContour, reference, reference, isControl: true)
        };
        for (var k = 0; k < prompts.Count; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double)k / prompts.Count);
            var p = prompts[k];
            int px = Math.Clamp((int)p.X, 0, w - 1), py = Math.Clamp((int)p.Y, 0, h - 1);
            if (candidates.Any(c => c.Mask.Contains(px, py))) continue;
            if (await MaskAt(encoded, p, w, h, cancellationToken).ConfigureAwait(false) is not { } found) continue;
            var (crop, score, contour) = found;
            var surface = Surface(frame, crop);
            var candidate = Candidate(crop, score, contour, surface, reference, isControl: false);

            var duplicate = candidates.FindIndex(c => c.Mask.IoU(crop) >= options.NmsIouThreshold);
            if (duplicate < 0) candidates.Add(candidate);
            else if (!candidates[duplicate].IsControl && candidates[duplicate].Score < score) candidates[duplicate] = candidate;
        }
        progress?.Report(1);

        var fresh = candidates.Where(c => !existingHolds.Any(e => e.Length >= 3 && Contains(e, c.Centroid))).ToList();
        return new RouteHoldSearchResult(new RouteHoldSearch(reference, fresh, prompts.Count), null);
    }

    /// <summary>The accepted candidates as holds of the clean frame: their contours and the D034 colour samples (the wall
    /// ring without the clip's other holds — the existing ones and the other accepted ones), all at
    /// <paramref name="sourceTime"/>. Each hold keeps its candidate's id.</summary>
    public static IReadOnlyList<Hold> ToHolds(DecodedFrame frame, IReadOnlyList<RouteHoldCandidate> accepted,
        IReadOnlyList<ImmutableArray<NormalizedPoint>> existingHolds, MediaTime sourceTime)
    {
        var holds = new List<Hold>(accepted.Count);
        foreach (var candidate in accepted)
        {
            var others = existingHolds.Concat(accepted.Where(a => a.Id != candidate.Id).Select(a => a.Contour))
                .Select(c => (IReadOnlyList<NormalizedPoint>)c);
            var (surface, wall) = HoldColorSampler.Sample(frame, candidate.Mask.ToFrameMask(), others);
            holds.Add(new Hold(candidate.Id, candidate.Contour, surface, wall, sourceTime));
        }
        return holds;
    }

    /// <summary>The D034 candidate at <paramref name="prompt"/>: the first offered mask, its largest component and outline;
    /// null when every mask is hidden or the outline is degenerate.</summary>
    private static async Task<(MaskCrop Crop, double Score, ImmutableArray<NormalizedPoint> Contour)?> MaskAt(IHoldFrame encoded,
        PointD prompt, int w, int h, CancellationToken cancellationToken)
    {
        var set = await encoded.PredictAsync(new[] { prompt }, cancellationToken).ConfigureAwait(false);
        if (HoldCandidateRules.Choose(set) is not { } k) return null;
        var mask = set.Masks[k];
        if (mask.Width != w || mask.Height != h) return null;
        var (component, _) = MaskContour.LargestComponent(mask.Pixels, w, h);
        var contour = MaskContour.Outer(component, w, h);
        if (contour.Length < HoldMarkupRules.MinPoints || MaskCrop.From(component, w, h) is not { } crop) return null;
        return (crop, mask.Score, contour);
    }

    private static OkLabColor Surface(DecodedFrame frame, MaskCrop crop) =>
        HoldColorSampler.Sample(frame, crop.ToFrameMask(), Array.Empty<IReadOnlyList<NormalizedPoint>>()).Surface;

    private static RouteHoldCandidate Candidate(MaskCrop crop, double score, ImmutableArray<NormalizedPoint> contour,
        OkLabColor surface, OkLabColor reference, bool isControl) =>
        new(Guid.NewGuid(), contour, HoldProposalService.ComputeCentroid(contour), Math.Clamp(score, 0, 1),
            (double)crop.Area / ((long)crop.FrameWidth * crop.FrameHeight), surface, RouteHoldColor.Distance(surface, reference), crop,
            isControl);

    private static bool Contains(ImmutableArray<NormalizedPoint> polygon, NormalizedPoint p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
