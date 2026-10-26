using System.Collections.Immutable;
using System.Diagnostics;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.Core.Holds;

/// <summary>
/// Core implementation of the automatic hold-proposal pipeline (Phase 31, D047). Uses the existing
/// <see cref="IHoldSegmentationService"/> abstractions but does not depend on a concrete ML backend, so it can be unit
/// tested with a fake <see cref="IHoldFrame"/>.
/// </summary>
public sealed class HoldProposalService : IHoldProposalService
{
    public async Task<ProposalGenerationResult> GenerateProposalsAsync(
        IHoldFrame frame,
        ProposalPipelineOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        options.Validate();

        var width = frame.Width;
        var height = frame.Height;
        var grid = GridPoints(width, height, options.GridDivisions);
        var candidates = new List<HoldProposal>();

        double predictionLoopMs = 0;
        double postProcessMs = 0;
        foreach (var point in grid)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var predictSw = Stopwatch.StartNew();
            var maskSet = await frame.PredictAsync(new[] { point }, cancellationToken).ConfigureAwait(false);
            predictionLoopMs += predictSw.Elapsed.TotalMilliseconds;

            var postSw = Stopwatch.StartNew();
            var chosen = HoldCandidateRules.Choose(maskSet);
            if (chosen is not { } k)
            {
                postProcessMs += postSw.Elapsed.TotalMilliseconds;
                continue;
            }
            var mask = maskSet.Masks[k];
            var proposal = ProposalFromMask(mask, point, options.GridDivisions);
            postProcessMs += postSw.Elapsed.TotalMilliseconds;
            if (proposal is not null) candidates.Add(proposal);
        }

        var nmsSw = Stopwatch.StartNew();
        var kept = Nms(candidates, options.NmsIouThreshold);
        var ranked = kept.OrderByDescending(p => p.Score).ThenBy(p => p.Id).ToList();
        var top = ranked.Take(options.MaxProposals).ToList();
        var nmsMs = nmsSw.Elapsed.TotalMilliseconds;

        return new ProposalGenerationResult(top, grid.Count)
        {
            Diagnostics = new ProposalGenerationDiagnostics(
                DecodeMs: 0,
                PrepareFrameMs: 0,
                PredictionLoopMs: predictionLoopMs,
                PredictionCount: grid.Count,
                PostProcessMs: postProcessMs,
                NmsMs: nmsMs,
                TotalMs: predictionLoopMs + postProcessMs + nmsMs)
        };
    }

    private static HoldProposal? ProposalFromMask(HoldMask mask, PointD prompt, int gridDivisions)
    {
        var id = ProposalIdFromPrompt(prompt, gridDivisions);
        var contour = MaskToContour(mask);
        if (contour.Length < HoldMarkupRules.MinPoints) return null;
        var centroid = ComputeCentroid(contour);
        return new HoldProposal(id, contour, centroid, mask.Score, mask.AreaFraction);
    }

    /// <summary>A deterministic id for a proposal based on its grid prompt. Stable across identical runs.</summary>
    private static Guid ProposalIdFromPrompt(PointD prompt, int gridDivisions)
    {
        var hash = HashCode.Combine(prompt.X.GetHashCode(), prompt.Y.GetHashCode(), gridDivisions.GetHashCode());
        var bytes = new byte[16];
        BitConverter.GetBytes(hash).CopyTo(bytes, 0);
        BitConverter.GetBytes(hash).CopyTo(bytes, 4);
        BitConverter.GetBytes(hash).CopyTo(bytes, 8);
        BitConverter.GetBytes(hash).CopyTo(bytes, 12);
        return new Guid(bytes);
    }

    /// <summary>Grid points spanning the frame in source pixels, excluding the exact border.</summary>
    public static List<PointD> GridPoints(int width, int height, int divisions)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (divisions < 2) throw new ArgumentOutOfRangeException(nameof(divisions));

        var points = new List<PointD>(divisions * divisions);
        var stepX = (double)width / (divisions + 1);
        var stepY = (double)height / (divisions + 1);
        for (var iy = 1; iy <= divisions; iy++)
        {
            for (var ix = 1; ix <= divisions; ix++)
            {
                points.Add(new PointD(ix * stepX, iy * stepY));
            }
        }
        return points;
    }

    /// <summary>Greedy NMS by mask IoU, keeping the highest-score proposal and discarding overlaps.</summary>
    public static List<HoldProposal> Nms(IReadOnlyList<HoldProposal> proposals, double iouThreshold)
    {
        if (iouThreshold <= 0) return proposals.ToList();
        var ordered = proposals.OrderByDescending(p => p.Score).ThenBy(p => p.Id).ToList();
        var kept = new List<HoldProposal>();
        foreach (var candidate in ordered)
        {
            var overlap = false;
            foreach (var existing in kept)
            {
                if (MaskIoU(candidate, existing) >= iouThreshold)
                {
                    overlap = true;
                    break;
                }
            }
            if (!overlap) kept.Add(candidate);
        }
        return kept;
    }

    /// <summary>Intersection-over-union of two proposals' masks, reconstructed from their contours.</summary>
    /// <remarks>
    /// This is intentionally approximate: it rasterises the contours onto a common grid. The NMS threshold is a user
    /// tolerance, not a geometric invariant, so a small rasterisation error is acceptable. For production performance the
    /// mask pixels should be cached; Step 31.1 keeps only the contour to stay memory-light.
    /// </remarks>
    public static double MaskIoU(HoldProposal a, HoldProposal b)
    {
        var width = Math.Max(a.Contour.Max(p => p.X), b.Contour.Max(p => p.X));
        var height = Math.Max(a.Contour.Max(p => p.Y), b.Contour.Max(p => p.Y));
        if (width <= 0 || height <= 0) return 0;

        // Use a small fixed raster size for IoU; large enough for a reliable threshold judgement.
        const int rasterSize = 128;
        var rasterA = Rasterise(a.Contour, rasterSize, rasterSize, width, height);
        var rasterB = Rasterise(b.Contour, rasterSize, rasterSize, width, height);

        long intersection = 0, union = 0;
        for (var i = 0; i < rasterA.Length; i++)
        {
            if (rasterA[i] && rasterB[i])
            {
                intersection++;
                union++;
            }
            else if (rasterA[i] || rasterB[i])
            {
                union++;
            }
        }
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>Rasterise a normalized contour into a binary mask using the even-odd rule.</summary>
    private static bool[] Rasterise(ImmutableArray<NormalizedPoint> contour, int w, int h, double frameWidth, double frameHeight)
    {
        var pixels = new bool[w * h];
        var points = contour.Select(p => new PointD(p.X / frameWidth * w, p.Y / frameHeight * h)).ToArray();
        for (var y = 0; y < h; y++)
        {
            var crossings = new List<double>();
            for (var i = 0; i < points.Length; i++)
            {
                var p1 = points[i];
                var p2 = points[(i + 1) % points.Length];
                if ((p1.Y > y) != (p2.Y > y))
                {
                    var x = p1.X + (y - p1.Y) * (p2.X - p1.X) / (p2.Y - p1.Y);
                    crossings.Add(x);
                }
            }
            crossings.Sort();
            for (var k = 0; k < crossings.Count - 1; k += 2)
            {
                var x0 = (int)Math.Ceiling(crossings[k]);
                var x1 = (int)Math.Floor(crossings[k + 1]);
                for (var x = Math.Max(0, x0); x <= Math.Min(w - 1, x1); x++)
                {
                    pixels[y * w + x] = true;
                }
            }
        }
        return pixels;
    }

    /// <summary>Extract the outer contour of a binary mask as normalized source points.</summary>
    public static ImmutableArray<NormalizedPoint> MaskToContour(HoldMask mask) =>
        MaskContour.Outer(mask.Pixels, mask.Width, mask.Height);

    /// <summary>Area centroid of a polygon in normalized coordinates (shoelace formula).</summary>
    public static NormalizedPoint ComputeCentroid(ImmutableArray<NormalizedPoint> contour)
    {
        if (contour.Length == 0) return new NormalizedPoint(0, 0);
        double cx = 0, cy = 0, area = 0;
        for (var i = 0; i < contour.Length; i++)
        {
            var p0 = contour[i];
            var p1 = contour[(i + 1) % contour.Length];
            var cross = p0.X * p1.Y - p1.X * p0.Y;
            area += cross;
            cx += (p0.X + p1.X) * cross;
            cy += (p0.Y + p1.Y) * cross;
        }
        if (Math.Abs(area) < double.Epsilon)
        {
            // Degenerate polygon: fall back to the mean of vertices.
            cx = contour.Average(p => p.X);
            cy = contour.Average(p => p.Y);
            return new NormalizedPoint(cx, cy);
        }
        var factor = 1 / (3 * area);
        return new NormalizedPoint(cx * factor, cy * factor);
    }
}
