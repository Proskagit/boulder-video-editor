using System.Collections.Immutable;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public sealed class HoldProposalServiceTests
{
    private readonly HoldProposalService _service = new();

    #region Grid generation

    [Theory]
    [InlineData(100, 100, 2, 4)]
    [InlineData(100, 100, 4, 16)]
    [InlineData(1920, 1080, 16, 256)]
    public void GridPoints_count_matches_divisions_squared(int w, int h, int divisions, int expectedCount)
    {
        var grid = HoldProposalService.GridPoints(w, h, divisions);
        Assert.Equal(expectedCount, grid.Count);
    }

    [Fact]
    public void GridPoints_spans_frame_and_avoids_exact_border()
    {
        var grid = HoldProposalService.GridPoints(100, 100, 2);
        Assert.Contains(grid, p => p.X > 0 && p.X < 100 && p.Y > 0 && p.Y < 100);
        Assert.All(grid, p =>
        {
            Assert.True(p.X > 0 && p.X < 100);
            Assert.True(p.Y > 0 && p.Y < 100);
        });
    }

    [Fact]
    public void GridPoints_rejects_invalid_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HoldProposalService.GridPoints(0, 100, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => HoldProposalService.GridPoints(100, 100, 1));
    }

    #endregion

    #region Pipeline options validation

    [Fact]
    public void ProposalPipelineOptions_validates_defaults()
    {
        var options = new ProposalPipelineOptions();
        Assert.Same(options, options.Validate());
    }

    [Theory]
    [InlineData(1, 0.7, 30, "GridDivisions")]
    [InlineData(65, 0.7, 30, "GridDivisions")]
    [InlineData(16, -0.1, 30, "NmsIouThreshold")]
    [InlineData(16, 1.1, 30, "NmsIouThreshold")]
    [InlineData(16, 0.7, 5, "MaxProposals")]
    [InlineData(16, 0.7, 200, "MaxProposals")]
    public void ProposalPipelineOptions_rejects_invalid_values(int grid, double iou, int max, string paramName)
    {
        var options = new ProposalPipelineOptions { GridDivisions = grid, NmsIouThreshold = iou, MaxProposals = max };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
        Assert.Equal(paramName, ex.ParamName);
    }

    #endregion

    #region End-to-end with fake backend

    [Fact]
    public async Task GenerateProposalsAsync_empty_frame_returns_no_proposals()
    {
        var frame = new FakeHoldFrame(100, 100, (_, _) => EmptyMaskSet(100, 100));
        var result = await _service.GenerateProposalsAsync(frame, new ProposalPipelineOptions { GridDivisions = 4 });
        Assert.Empty(result.Proposals);
        Assert.Equal(16, result.GridPoints);
    }

    [Fact]
    public async Task GenerateProposalsAsync_finds_single_mask()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            // One 20×20 square mask near the centre, triggered by points inside it.
            if (x >= 40 && x < 60 && y >= 40 && y < 60)
                return SquareMaskSet(100, 100, 40, 40, 20, score: 0.95);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(frame, new ProposalPipelineOptions { GridDivisions = 8 });

        Assert.Single(result.Proposals);
        var proposal = result.Proposals[0];
        Assert.Equal(0.95, proposal.Score);
        Assert.InRange(proposal.AreaFraction, 0.03, 0.05);
        Assert.InRange(proposal.Centroid.X, 0.49, 0.51);
        Assert.InRange(proposal.Centroid.Y, 0.49, 0.51);
        Assert.True(proposal.Contour.Length >= 3);
    }

    [Fact]
    public async Task GenerateProposalsAsync_ranks_by_score_and_limits_max_proposals()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            // Two separate squares: left with lower score, right with higher score.
            if (x >= 10 && x < 20 && y >= 40 && y < 50)
                return SquareMaskSet(100, 100, 10, 40, 10, score: 0.6);
            if (x >= 70 && x < 80 && y >= 40 && y < 50)
                return SquareMaskSet(100, 100, 70, 40, 10, score: 0.9);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(
            frame,
            new ProposalPipelineOptions { GridDivisions = 8, MaxProposals = 10 });

        Assert.True(result.Proposals.Count >= 2);
        Assert.Equal(0.9, result.Proposals[0].Score);
        Assert.True(result.Proposals.All(p => p.Score <= result.Proposals[0].Score));
    }

    [Fact]
    public async Task GenerateProposalsAsync_limits_proposals_to_max_proposals()
    {
        // Every grid point produces a small non-overlapping mask, so NMS does not collapse them.
        // 4×4 grid = 16 candidates; ask for only 10.
        var frame = new FakeHoldFrame(64, 64, (x, y) =>
        {
            var cellX = x / 16;
            var cellY = y / 16;
            var originX = cellX * 16 + 4;
            var originY = cellY * 16 + 4;
            return SquareMaskSet(64, 64, originX, originY, 8, score: 0.9);
        });

        var result = await _service.GenerateProposalsAsync(
            frame,
            new ProposalPipelineOptions { GridDivisions = 4, MaxProposals = 10, NmsIouThreshold = 0.1 });

        Assert.Equal(10, result.Proposals.Count);
        Assert.True(result.Proposals.Zip(result.Proposals.Skip(1), (a, b) => a.Score >= b.Score).All(ok => ok));
    }

    [Fact]
    public async Task GenerateProposalsAsync_nms_suppresses_overlapping_duplicates()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            // Many grid points inside one large square all produce overlapping masks.
            if (x >= 30 && x < 70 && y >= 30 && y < 70)
                return SquareMaskSet(100, 100, 30, 30, 40, score: 0.95);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(
            frame,
            new ProposalPipelineOptions { GridDivisions = 8, NmsIouThreshold = 0.5, MaxProposals = 100 });

        // All prompts inside the square should collapse to one proposal after NMS.
        Assert.Single(result.Proposals);
    }

    [Fact]
    public async Task GenerateProposalsAsync_keeps_separate_non_overlapping_masks()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            if (x >= 10 && x < 20 && y >= 40 && y < 50)
                return SquareMaskSet(100, 100, 10, 40, 10, score: 0.95);
            if (x >= 70 && x < 80 && y >= 40 && y < 50)
                return SquareMaskSet(100, 100, 70, 40, 10, score: 0.95);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(
            frame,
            new ProposalPipelineOptions { GridDivisions = 8, NmsIouThreshold = 0.7, MaxProposals = 100 });

        Assert.Equal(2, result.Proposals.Count);
    }

    [Fact]
    public async Task GenerateProposalsAsync_hides_masks_larger_than_candidate_rules()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            // A 50×50 mask (50 % of frame) with low score: should be hidden.
            if (x >= 25 && x < 75 && y >= 25 && y < 75)
                return SquareMaskSet(100, 100, 25, 25, 50, score: 0.5);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(frame, new ProposalPipelineOptions { GridDivisions = 4 });
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public async Task GenerateProposalsAsync_keeps_large_confident_masks()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) =>
        {
            // A 15×15 mask (2.25 %) with high score is offered by the confident-large rule.
            if (x >= 40 && x < 60 && y >= 40 && y < 60)
                return SquareMaskSet(100, 100, 40, 40, 15, score: 0.95);
            return EmptyMaskSet(100, 100);
        });

        var result = await _service.GenerateProposalsAsync(frame, new ProposalPipelineOptions { GridDivisions = 4 });
        Assert.Single(result.Proposals);
    }

    [Fact]
    public async Task GenerateProposalsAsync_respects_cancellation()
    {
        var frame = new FakeHoldFrame(100, 100, (x, y) => SquareMaskSet(100, 100, 0, 0, 10, score: 0.9));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.GenerateProposalsAsync(frame, new ProposalPipelineOptions { GridDivisions = 4 }, cts.Token));
    }

    #endregion

    #region Mask-to-contour and centroid

    [Fact]
    public void MaskToContour_extracts_square_contour()
    {
        var mask = SquareMask(100, 100, 10, 10, 10);
        var contour = HoldProposalService.MaskToContour(mask);
        Assert.True(contour.Length >= 4);
        Assert.All(contour, p =>
        {
            Assert.InRange(p.X, 0.09, 0.21);
            Assert.InRange(p.Y, 0.09, 0.21);
        });
    }

    [Fact]
    public void MaskToContour_returns_empty_for_empty_mask()
    {
        var mask = EmptyMask(100, 100);
        var contour = HoldProposalService.MaskToContour(mask);
        Assert.Empty(contour);
    }

    [Fact]
    public void ComputeCentroid_square_is_at_centre()
    {
        var contour = ImmutableArray.Create(
            new NormalizedPoint(0.1, 0.1),
            new NormalizedPoint(0.2, 0.1),
            new NormalizedPoint(0.2, 0.2),
            new NormalizedPoint(0.1, 0.2));
        var centroid = HoldProposalService.ComputeCentroid(contour);
        Assert.Equal(0.15, centroid.X, precision: 5);
        Assert.Equal(0.15, centroid.Y, precision: 5);
    }

    [Fact]
    public void Nms_keeps_highest_score_when_overlap_exceeds_threshold()
    {
        var a = CreateProposal(score: 0.9, areaFraction: 0.01, square: (10, 10, 20));
        var b = CreateProposal(score: 0.8, areaFraction: 0.01, square: (12, 12, 20)); // heavily overlaps a
        var iou = HoldProposalService.MaskIoU(a, b);
        Assert.True(iou > 0.5, $"Expected IoU > 0.5, got {iou}");
        var result = HoldProposalService.Nms(new[] { a, b }, 0.5);
        Assert.Single(result);
        Assert.Equal(0.9, result[0].Score);
    }

    [Fact]
    public void Nms_keeps_both_when_overlap_is_below_threshold()
    {
        var a = CreateProposal(score: 0.9, areaFraction: 0.01, square: (10, 10, 10));
        var b = CreateProposal(score: 0.8, areaFraction: 0.01, square: (80, 80, 10));
        var result = HoldProposalService.Nms(new[] { a, b }, 0.7);
        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Helpers

    private static HoldMask EmptyMask(int w, int h) => new(w, h, new byte[w * h], 0);

    private static HoldMaskSet EmptyMaskSet(int w, int h) => new(ImmutableArray.Create(EmptyMask(w, h)));

    private static HoldMask SquareMask(int w, int h, int x, int y, int size, double score = 0.9)
    {
        var pixels = new byte[w * h];
        for (var iy = y; iy < y + size && iy < h; iy++)
            for (var ix = x; ix < x + size && ix < w; ix++)
                pixels[iy * w + ix] = 1;
        return new HoldMask(w, h, pixels, score);
    }

    private static HoldMaskSet SquareMaskSet(int w, int h, int x, int y, int size, double score = 0.9) =>
        new(ImmutableArray.Create(SquareMask(w, h, x, y, size, score)));

    private static HoldProposal CreateProposal(double score, double areaFraction, (int X, int Y, int Size) square)
    {
        var (x, y, size) = square;
        var mask = SquareMask(100, 100, x, y, size, score);
        var contour = HoldProposalService.MaskToContour(mask);
        return new HoldProposal(
            Guid.NewGuid(),
            contour,
            HoldProposalService.ComputeCentroid(contour),
            score,
            areaFraction);
    }

    private sealed class FakeHoldFrame : IHoldFrame
    {
        private readonly int _width;
        private readonly int _height;
        private readonly Func<int, int, HoldMaskSet> _predictor;
        private bool _disposed;

        public FakeHoldFrame(int width, int height, Func<int, int, HoldMaskSet> predictor)
        {
            _width = width;
            _height = height;
            _predictor = predictor;
        }

        public int Width => _width;
        public int Height => _height;

        public Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (points.Count == 0) throw new ArgumentException("At least one point is required.", nameof(points));
            var p = points[0];
            return Task.FromResult(_predictor((int)p.X, (int)p.Y));
        }

        public void Dispose() => _disposed = true;
    }

    #endregion
}
