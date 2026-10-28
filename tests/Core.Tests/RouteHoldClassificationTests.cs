using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D048 stage 1 (§2), the colour classification on the clean frame (<c>PHASE_HOLD_TRACKING.md</c> S1–S2) on a synthetic
/// wall: the reference colour from the control hold's mask (a highlight inside it does not move it), the colour-map prompts
/// (one per region, inside it), the D047 grid as an extra prompt source, the selection rule (tolerance, score, the control
/// hold, rejections, fragments) and its monotonic tolerance without model calls, the holds already marked left out, a click
/// beside any hold → <see cref="Messages.RouteSelectionNoControlHold"/>, the accepted holds with their samples and time.
/// </summary>
public class RouteHoldClassificationTests
{
    internal const int W = 480, H = 270;
    internal static readonly (byte R, byte G, byte B) WallColor = (205, 195, 175);
    internal static readonly (byte R, byte G, byte B) Red = (200, 35, 40);
    internal static readonly (byte R, byte G, byte B) Coral = (225, 125, 75);
    internal static readonly (byte R, byte G, byte B) Blue = (40, 70, 200);

    /// <summary>The route: four red holds (the third with a white highlight), one coral hold of a similar colour, two
    /// blue holds of another route.</summary>
    internal static readonly (int X, int Y, int R)[] RedHolds = { (60, 60, 12), (160, 200, 10), (300, 80, 14), (420, 200, 8) };
    internal static readonly (int X, int Y, int R) CoralHold = (240, 150, 10);
    internal static readonly (int X, int Y, int R)[] BlueHolds = { (100, 225, 10), (380, 40, 10) };
    internal static readonly (int X, int Y, int R) Highlight = (303, 77, 3);

    internal static DecodedFrame Scene()
    {
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var c = WallColor;
            foreach (var h in RedHolds) if (Inside(h, x, y)) c = Red;
            if (Inside(CoralHold, x, y)) c = Coral;
            foreach (var h in BlueHolds) if (Inside(h, x, y)) c = Blue;
            if (Inside(Highlight, x, y)) c = (250, 250, 250);
            var i = (y * W + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
        }
        return new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(0, new TimeBase(1, 25)));
    }

    private static bool Inside((int X, int Y, int R) disc, int x, int y) =>
        (x - disc.X) * (x - disc.X) + (y - disc.Y) * (y - disc.Y) <= disc.R * disc.R;

    private static NormalizedPoint At((int X, int Y, int R) disc) => new((disc.X + 0.5) / W, (disc.Y + 0.5) / H);

    private static bool Covers(RouteHoldCandidate c, (int X, int Y, int R) disc) => c.Mask.Contains(disc.X + disc.R / 2, disc.Y);

    [Fact]
    public void The_distance_weighs_the_lightness_by_half()
    {
        Assert.Equal(0.05, RouteHoldColor.Distance(new OkLabColor(0.5, 0, 0), new OkLabColor(0.6, 0, 0)), 12);
        Assert.Equal(0.1, RouteHoldColor.Distance(new OkLabColor(0.5, 0, 0), new OkLabColor(0.5, 0.06, 0.08)), 12);
    }

    [Fact]
    public void The_colour_map_puts_one_prompt_inside_each_region_of_the_reference_colour_deterministically()
    {
        var frame = Scene();
        var red = RouteColor.SrgbToOkLab(Red.R, Red.G, Red.B);
        var prompts = ColorPromptPlanner.Plan(frame, red, threshold: 0.05);

        Assert.Equal(RedHolds.Length, prompts.Count);
        foreach (var hold in RedHolds)
            Assert.Single(prompts, p => Inside(hold, (int)p.X, (int)p.Y));
        Assert.DoesNotContain(prompts, p => BlueHolds.Any(b => Inside(b, (int)p.X, (int)p.Y)) || Inside(Highlight, (int)p.X, (int)p.Y));
        var plain = prompts.Single(p => Inside(RedHolds[0], (int)p.X, (int)p.Y));      // the innermost point: the centre
        Assert.True(Math.Abs(plain.X - RedHolds[0].X - 0.5) <= 1 && Math.Abs(plain.Y - RedHolds[0].Y - 0.5) <= 1, $"{plain}");
        Assert.Equal(prompts, ColorPromptPlanner.Plan(frame, red, threshold: 0.05));
        Assert.Equal(prompts.Take(2), ColorPromptPlanner.Plan(frame, red, threshold: 0.05, maxPrompts: 2));   // the largest first
        Assert.True(Inside(RedHolds[2], (int)prompts[0].X, (int)prompts[0].Y));
    }

    [Fact]
    public async Task The_control_holds_colour_selects_the_route_and_not_the_other_colours()
    {
        var model = new FloodModel(Scene());
        var search = await Find(model, At(RedHolds[0]));

        var selected = search.Select(RouteHoldColor.DefaultTolerance);
        Assert.True(selected[0].IsControl);
        Assert.True(Covers(selected[0], RedHolds[0]));
        foreach (var hold in RedHolds) Assert.Single(selected, c => Covers(c, hold));
        Assert.DoesNotContain(selected, c => BlueHolds.Any(b => Covers(c, b)) || Covers(c, CoralHold));
        Assert.Equal(RedHolds.Length, selected.Count);
    }

    [Fact]
    public async Task A_highlight_inside_the_control_hold_does_not_move_the_reference()
    {
        var plain = await Find(new FloodModel(Scene()), At(RedHolds[0]));
        var lit = await Find(new FloodModel(Scene()), new NormalizedPoint((RedHolds[2].X - 8.5) / W, (RedHolds[2].Y + 0.5) / H));

        Assert.True(RouteHoldColor.Distance(plain.Reference, lit.Reference) < 0.01,
            $"{RouteHoldColor.Distance(plain.Reference, lit.Reference)}");
        Assert.Equal(plain.Select(RouteHoldColor.DefaultTolerance).Count, lit.Select(RouteHoldColor.DefaultTolerance).Count);
    }

    [Fact]
    public async Task The_tolerance_widens_the_set_monotonically_without_calling_the_model()
    {
        var model = new FloodModel(Scene());
        var search = await Find(model, At(RedHolds[0]));
        var calls = model.Calls;

        var previous = new HashSet<Guid>();
        for (var tolerance = RouteHoldColor.MinTolerance; tolerance <= RouteHoldColor.MaxTolerance + 1e-9; tolerance += 0.01)
        {
            var ids = search.Select(tolerance).Select(c => c.Id).ToHashSet();
            Assert.True(previous.IsSubsetOf(ids), $"τ = {tolerance}");
            previous = ids;
        }
        Assert.Equal(calls, model.Calls);                                                      // no model call
        Assert.DoesNotContain(search.Select(RouteHoldColor.DefaultTolerance), c => Covers(c, CoralHold));
        Assert.Contains(search.Select(RouteHoldColor.MaxTolerance), c => Covers(c, CoralHold));   // a similar colour, wider
        Assert.DoesNotContain(search.Select(RouteHoldColor.MaxTolerance), c => BlueHolds.Any(b => Covers(c, b)));
    }

    [Fact]
    public async Task Rejected_proposals_never_return_and_the_control_hold_is_kept_at_any_tolerance()
    {
        var search = await Find(new FloodModel(Scene()), At(RedHolds[0]));
        var other = search.Select(RouteHoldColor.DefaultTolerance).First(c => !c.IsControl);
        var rejected = new HashSet<Guid> { other.Id };

        Assert.DoesNotContain(search.Select(RouteHoldColor.MaxTolerance, rejected), c => c.Id == other.Id);
        Assert.Contains(search.Select(RouteHoldColor.MinTolerance), c => c.IsControl);
    }

    [Fact]
    public void A_fragment_inside_a_larger_selected_mask_and_a_low_score_mask_are_not_selected()
    {
        var reference = new OkLabColor(0.6, 0.2, 0.1);
        RouteHoldCandidate Make(int x0, int y0, int size, double score, bool control = false)
        {
            var mask = new byte[W * H];
            for (var y = y0; y < y0 + size; y++)
            for (var x = x0; x < x0 + size; x++)
                mask[y * W + x] = 1;
            var crop = MaskCrop.From(mask, W, H)!;
            return new RouteHoldCandidate(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0, 0), new NormalizedPoint(0.1, 0),
                new NormalizedPoint(0, 0.1)), new NormalizedPoint((x0 + size / 2.0) / W, (y0 + size / 2.0) / H), score,
                crop.Area / (double)(W * H), reference, 0.01, crop, control);
        }

        var control = Make(10, 10, 10, 0.3, control: true);    // the user's click: kept whatever its score
        var large = Make(100, 100, 30, 0.9);
        var fragment = Make(105, 105, 10, 0.95);                 // wholly inside the large one
        var weak = Make(200, 100, 10, 0.4);                      // below the score
        var selected = RouteHoldSelector.Select(new[] { control, large, fragment, weak }, RouteHoldColor.DefaultTolerance);

        Assert.Equal(new[] { control.Id, large.Id }, selected.Select(c => c.Id));
    }

    [Fact]
    public async Task The_D047_grid_adds_its_prompts_to_the_colour_maps()
    {
        var withGrid = new FloodModel(Scene());
        var search = await Find(withGrid, At(RedHolds[0]));
        var colourOnly = new FloodModel(Scene());
        var plain = await Find(colourOnly, At(RedHolds[0]), new RouteHoldSearchOptions { UseGrid = false });

        var grid = HoldProposalService.GridPoints(W, H, new ProposalPipelineOptions().GridDivisions);
        Assert.Equal(plain.Prompts + grid.Count, search.Prompts);
        Assert.Contains(withGrid.Points, p => grid.Contains(p));
        Assert.DoesNotContain(colourOnly.Points, p => grid.Contains(p));
        Assert.Equal(plain.Select(RouteHoldColor.DefaultTolerance).Count, search.Select(RouteHoldColor.DefaultTolerance).Count);
    }

    [Fact]
    public async Task Holds_the_clip_already_has_are_left_out_so_a_second_run_adds_no_duplicates()
    {
        var first = await Find(new FloodModel(Scene()), At(RedHolds[0]));
        var marked = first.Select(RouteHoldColor.DefaultTolerance).Where(c => !Covers(c, RedHolds[3])).Select(c => c.Contour).ToList();

        var second = await Find(new FloodModel(Scene()), At(RedHolds[0]), existing: marked);
        var fresh = second.Select(RouteHoldColor.DefaultTolerance);
        Assert.True(Covers(Assert.Single(fresh), RedHolds[3]));
    }

    [Fact]
    public async Task A_click_beside_any_hold_is_an_error_and_cancellation_leaves_no_result()
    {
        var wall = await RouteHoldFinder.FindAsync(new FloodModel(Scene()), Scene(), new NormalizedPoint(0.5, 0.02),
            Array.Empty<ImmutableArray<NormalizedPoint>>(), new RouteHoldSearchOptions());
        Assert.Null(wall.Search);
        Assert.Equal(Messages.RouteSelectionNoControlHold, wall.Error);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RouteHoldFinder.FindAsync(new FloodModel(Scene()), Scene(),
            At(RedHolds[0]), Array.Empty<ImmutableArray<NormalizedPoint>>(), new RouteHoldSearchOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public async Task The_accepted_candidates_become_holds_of_the_clean_frame_with_their_samples()
    {
        var frame = Scene();
        var search = await Find(new FloodModel(frame), At(RedHolds[0]));
        var accepted = search.Select(RouteHoldColor.DefaultTolerance);
        var time = MediaTime.FromSeconds(3.2);

        var holds = RouteHoldFinder.ToHolds(frame, accepted, Array.Empty<ImmutableArray<NormalizedPoint>>(), time);

        Assert.Equal(accepted.Select(c => c.Id), holds.Select(h => h.Id));
        var red = RouteColor.SrgbToOkLab(Red.R, Red.G, Red.B);
        var wall = RouteColor.SrgbToOkLab(WallColor.R, WallColor.G, WallColor.B);
        Assert.All(holds, h =>
        {
            Assert.Equal(time, h.SourceTime);
            Assert.Null(HoldMarkupRules.ValidateHold(h));
            Assert.True(h.SurfaceLab.DistanceTo(red) < 0.01);
            Assert.True(h.WallLab.DistanceTo(wall) < 0.01);
        });
    }

    private static async Task<RouteHoldSearch> Find(FloodModel model, NormalizedPoint control, RouteHoldSearchOptions? options = null,
        IReadOnlyList<ImmutableArray<NormalizedPoint>>? existing = null)
    {
        var result = await RouteHoldFinder.FindAsync(model, model.Frame, control, existing ?? Array.Empty<ImmutableArray<NormalizedPoint>>(),
            options ?? new RouteHoldSearchOptions());
        Assert.True(result.Search is not null, result.Error);
        return result.Search!;
    }

    /// <summary>A stand-in for the model on the synthetic scene: the mask of a prompt is the 4-connected region of the
    /// prompt's exact colour (the wall is far over 20 % of the frame — hidden by the D034 rule, so a click on it finds
    /// nothing).</summary>
    internal sealed class FloodModel(DecodedFrame frame, double score = 0.9) : IHoldFrame
    {
        public DecodedFrame Frame => frame;
        public int Width => frame.Width;
        public int Height => frame.Height;
        public int Calls { get; private set; }
        public List<PointD> Points { get; } = new();

        public Task<HoldMaskSet> PredictAsync(IReadOnlyList<PointD> points, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Points.Add(points[0]);
            int w = frame.Width, h = frame.Height;
            int sx = Math.Clamp((int)points[0].X, 0, w - 1), sy = Math.Clamp((int)points[0].Y, 0, h - 1);
            var px = frame.Pixels.Span;
            var seed = Color(px, sx, sy);
            var mask = new byte[w * h];
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue((sx, sy));
            mask[sy * w + sx] = 1;
            while (queue.Count > 0)
            {
                var (x, y) = queue.Dequeue();
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || mask[ny * w + nx] != 0 || Color(px, nx, ny) != seed) continue;
                    mask[ny * w + nx] = 1;
                    queue.Enqueue((nx, ny));
                }
            }
            var m = new HoldMask(w, h, mask, score);
            return Task.FromResult(new HoldMaskSet(ImmutableArray.Create(m, m, m)));

            int Color(ReadOnlySpan<byte> p, int x, int y) => p[(y * w + x) * 4] | p[(y * w + x) * 4 + 1] << 8 | p[(y * w + x) * 4 + 2] << 16;
        }

        public void Dispose() { }
    }
}
