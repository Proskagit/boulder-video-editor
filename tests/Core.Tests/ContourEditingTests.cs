using System.Collections.Immutable;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Holds;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 20 (D035): the geometry of the manual contour correction — moving, inserting and removing vertices, at least three
/// vertices, no self-crossing, and the simplified handles of a dense contour that keep every stored vertex.
/// </summary>
public class ContourEditingTests
{
    private const int W = 1000, H = 500;

    private static NormalizedPoint P(double x, double y) => new(x, y);

    private static ImmutableArray<NormalizedPoint> Square() => ImmutableArray.Create(P(0.4, 0.4), P(0.6, 0.4), P(0.6, 0.6), P(0.4, 0.6));

    private static ContourEdit All(ImmutableArray<NormalizedPoint> points) => new(points, ContourEditing.Handles(points, W, H));

    /// <summary>A circle of <paramref name="n"/> vertices (a dense traced contour).</summary>
    private static ImmutableArray<NormalizedPoint> Circle(int n, double r = 0.1) =>
        Enumerable.Range(0, n).Select(i => P(0.5 + r * Math.Cos(2 * Math.PI * i / n), 0.5 + r * 2 * Math.Sin(2 * Math.PI * i / n))).ToImmutableArray();

    [Fact]
    public void A_small_contour_shows_every_vertex_and_moving_one_moves_only_it()
    {
        var edit = All(Square());
        Assert.Equal(new[] { 0, 1, 2, 3 }, edit.Handles);

        var moved = ContourEditing.MoveHandle(edit, 1, P(0.7, 0.35), W, H);
        Assert.Equal(P(0.7, 0.35), moved.Points[1]);
        Assert.Equal(new[] { edit.Points[0], edit.Points[2], edit.Points[3] }, new[] { moved.Points[0], moved.Points[2], moved.Points[3] });
        Assert.Equal(edit.Handles, moved.Handles);
        Assert.False(ContourEditing.IntroducesCrossing(edit.Points, moved.Points));
    }

    [Fact]
    public void A_moved_vertex_is_clamped_to_the_frame()
    {
        var moved = ContourEditing.MoveHandle(All(Square()), 2, P(1.4, -0.2), W, H);
        Assert.Equal(P(1, 0), moved.Points[2]);
    }

    [Fact]
    public void A_dense_contour_shows_a_coarser_subset_of_handles_and_a_drag_keeps_every_vertex()
    {
        var circle = Circle(400);
        var handles = ContourEditing.Handles(circle, W, H);
        Assert.InRange(handles.Length, ContourEditing.MinHandles, 40);              // simplified for display
        Assert.True(handles.SequenceEqual(handles.Order()));

        var edit = new ContourEdit(circle, handles);
        var h = 3;
        var (previous, index, next) = (handles[h - 1], handles[h], handles[h + 1]);
        var target = P(circle[index].X + 0.02, circle[index].Y + 0.01);
        var moved = ContourEditing.MoveHandle(edit, h, target, W, H);

        Assert.Equal(circle.Length, moved.Points.Length);                            // no vertex lost
        Assert.Equal(target, moved.Points[index]);
        for (var i = 0; i < circle.Length; i++)
        {
            var between = i > previous && i < next;
            if (!between) Assert.Equal(circle[i], moved.Points[i]);                  // outside the two stretches: exact
            else if (i != index)
            {
                // The hidden vertices follow by a share of the move, never more than the handle itself.
                var dx = moved.Points[i].X - circle[i].X;
                Assert.InRange(dx, 1e-9, 0.02);
            }
        }
        Assert.False(ContourEditing.IntroducesCrossing(circle, moved.Points));
    }

    [Fact]
    public void Handles_are_thinned_to_a_spacing_on_the_screen_but_never_below_three()
    {
        var circle = Circle(400);                                                   // radius 100 frame pixels
        var spaced = ContourEditing.Handles(circle, W, H, minSpacingPixels: 60);
        Assert.InRange(spaced.Length, ContourEditing.MinHandles, ContourEditing.Handles(circle, W, H).Length);
        for (var i = 0; i < spaced.Length; i++)
        {
            var (a, b) = (circle[spaced[i]], circle[spaced[(i + 1) % spaced.Length]]);
            Assert.True(Math.Sqrt(Math.Pow((a.X - b.X) * W, 2) + Math.Pow((a.Y - b.Y) * H, 2)) >= 60 - 1e-9);
        }

        // Far more than the contour's size: still three, spread along it; a small contour too.
        Assert.Equal(3, ContourEditing.Handles(circle, W, H, minSpacingPixels: 10_000).Length);
        Assert.Equal(3, ContourEditing.Handles(Square(), W, H, minSpacingPixels: 10_000).Length);
        Assert.Equal(4, ContourEditing.Handles(Square(), W, H, minSpacingPixels: 1).Length);
    }

    [Fact]
    public void Inserting_puts_a_vertex_on_the_edge_and_makes_it_a_handle()
    {
        var edit = All(Square());
        var inserted = ContourEditing.InsertVertex(edit, 1, 0.25, W, H)!;
        Assert.Equal(5, inserted.Points.Length);
        Assert.Equal(0.6, inserted.Points[2].X, 12);
        Assert.Equal(0.45, inserted.Points[2].Y, 12);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, inserted.Handles);
        Assert.False(ContourEditing.IntroducesCrossing(edit.Points, inserted.Points));

        // On a dense contour the other handles shift past the new vertex.
        var circle = Circle(400);
        var dense = new ContourEdit(circle, ContourEditing.Handles(circle, W, H));
        var after = ContourEditing.InsertVertex(dense, dense.Handles[2], 0.5, W, H)!;
        Assert.Equal(circle.Length + 1, after.Points.Length);
        Assert.Equal(dense.Handles.Length + 1, after.Handles.Length);
        Assert.Contains(dense.Handles[2] + 1, after.Handles);
        Assert.Equal(dense.Handles.Select(i => after.Points[i > dense.Handles[2] ? i + 1 : i]), dense.Handles.Select(i => circle[i]));
    }

    [Fact]
    public void Inserting_beyond_the_point_limit_is_refused()
    {
        var full = Circle(HoldMarkupRules.MaxPoints, 0.4);
        Assert.Null(ContourEditing.InsertVertex(new ContourEdit(full, ImmutableArray.Create(0, 100, 200)), 0, 0.5, 100_000, 50_000));
    }

    [Fact]
    public void Inserting_on_a_vertex_of_a_dense_contour_makes_that_vertex_a_handle_without_changing_the_points()
    {
        var circle = Circle(400);                                                   // edges of about 1.6 frame pixels
        var dense = new ContourEdit(circle, ContourEditing.Handles(circle, W, H));
        var hidden = Enumerable.Range(0, circle.Length).First(i => !dense.Handles.Contains(i));
        var promoted = ContourEditing.InsertVertex(dense, hidden, 0.1, W, H)!;
        Assert.True(promoted.Points == circle);                                     // the same array: no edit
        Assert.Contains(hidden, promoted.Handles);
        Assert.True(promoted.Handles.SequenceEqual(promoted.Handles.Order()));
        Assert.Null(ContourEditing.InsertVertex(promoted, hidden, 0.1, W, H));      // a handle already
    }

    [Fact]
    public void Removing_a_vertex_keeps_at_least_three()
    {
        var pentagon = ContourEditing.InsertVertex(All(Square()), 0, 0.5, W, H)!;
        var square = ContourEditing.RemoveHandle(pentagon, 1)!;
        Assert.Equal(Square().ToArray(), square.Points.ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3 }, square.Handles);

        var triangle = ContourEditing.RemoveHandle(square, 0)!;
        Assert.Equal(3, triangle.Points.Length);
        Assert.Null(ContourEditing.RemoveHandle(triangle, 1));                         // never below three
    }

    [Fact]
    public void Removing_a_handle_of_a_dense_contour_straightens_only_the_stretch_between_its_neighbours()
    {
        var circle = Circle(400);
        var edit = new ContourEdit(circle, ContourEditing.Handles(circle, W, H));
        var (previous, next) = (edit.Handles[2], edit.Handles[4]);
        var removed = ContourEditing.RemoveHandle(edit, 3)!;

        Assert.Equal(circle.Length - (next - previous - 1), removed.Points.Length);
        Assert.Equal(edit.Handles.Length - 1, removed.Handles.Length);
        Assert.Equal(circle[previous], removed.Points[previous]);
        Assert.Equal(circle[next], removed.Points[previous + 1]);                       // a straight edge between them
        Assert.Equal(circle.Take(previous + 1), removed.Points.Take(previous + 1));
        Assert.Equal(circle.Skip(next), removed.Points.Skip(previous + 1));             // the rest exactly as it was
    }

    [Fact]
    public void A_vertex_dragged_across_the_contour_is_a_crossing()
    {
        var edit = All(Square());
        var bowtie = ContourEditing.MoveHandle(edit, 1, P(0.45, 0.7), W, H);  // 0.6,0.4 dragged across the bottom edge
        Assert.True(ContourEditing.IntroducesCrossing(edit.Points, bowtie.Points));

        var touching = ContourEditing.MoveHandle(edit, 1, P(0.5, 0.6), W, H); // onto the bottom edge
        Assert.True(ContourEditing.IntroducesCrossing(edit.Points, touching.Points));

        var onNeighbour = ContourEditing.MoveHandle(edit, 1, P(0.6, 0.6), W, H); // onto the next vertex
        Assert.True(ContourEditing.IntroducesCrossing(edit.Points, onNeighbour.Points));
    }

    [Fact]
    public void A_flat_contour_has_no_area_and_is_refused()
    {
        var triangle = ImmutableArray.Create(P(0.1, 0.1), P(0.5, 0.1), P(0.3, 0.4));
        var flat = ContourEditing.MoveHandle(All(triangle), 2, P(0.3, 0.1), W, H);
        Assert.True(ContourEditing.IntroducesCrossing(triangle, flat.Points));
    }

    [Fact]
    public void A_crossing_the_contour_already_had_never_blocks_an_edit_elsewhere()
    {
        // A traced mask can touch itself at a one-pixel neck: the edges 1-2 and 5-6 share a stretch.
        var necked = ImmutableArray.Create(P(0.1, 0.1), P(0.3, 0.1), P(0.3, 0.3), P(0.5, 0.3), P(0.5, 0.5), P(0.3, 0.5), P(0.3, 0.2), P(0.1, 0.3));
        var edit = new ContourEdit(necked, Enumerable.Range(0, necked.Length).ToImmutableArray());
        var elsewhere = ContourEditing.MoveHandle(edit, 4, P(0.55, 0.55), W, H);
        Assert.False(ContourEditing.IntroducesCrossing(necked, elsewhere.Points));
        Assert.True(ContourEditing.IntroducesCrossing(necked, ContourEditing.MoveHandle(edit, 4, P(0.2, 0.05), W, H).Points));
    }

    [Fact]
    public void The_nearest_edge_and_its_parameter()
    {
        var square = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var (edge, t, distance) = ContourEditing.NearestEdge(square, (7.5, 1));
        Assert.Equal(0, edge);
        Assert.Equal(0.75, t, 12);
        Assert.Equal(1, distance, 12);
        Assert.Equal(2, ContourEditing.NearestEdge(square, (4, 11)).Edge);
    }
}
