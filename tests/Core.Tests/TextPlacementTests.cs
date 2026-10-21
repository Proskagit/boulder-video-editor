using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>Phase 27 (D043 §3): the 3 × 3 placement grid — the drawn box at 8 % of the short side from its cell's edges,
/// centred on the middle row / centre column; the background's padding or the outline's width grow the box; the scale and
/// a rotation give its axis-aligned bounds; portrait and landscape canvases.</summary>
public class TextPlacementTests
{
    private static TextProperties Plain(double size = 50) => new("Title", "Segoe UI", size, "#FFFFFF", TextAlignment.Center);

    public static TheoryData<int, int, TextGridRow, TextGridColumn, double, double> Cells()
    {
        var data = new TheoryData<int, int, TextGridRow, TextGridColumn, double, double>();
        foreach (var (w, h) in new[] { (1080, 1920), (1920, 1080) })
        {
            var m = 0.08 * Math.Min(w, h);
            foreach (var row in Enum.GetValues<TextGridRow>())
            foreach (var col in Enum.GetValues<TextGridColumn>())
            {
                // box 300 × 100
                var x = col switch { TextGridColumn.Left => -w / 2.0 + m + 150, TextGridColumn.Right => w / 2.0 - m - 150, _ => 0 };
                var y = row switch { TextGridRow.Top => -h / 2.0 + m + 50, TextGridRow.Bottom => h / 2.0 - m - 50, _ => 0 };
                data.Add(w, h, row, col, x, y);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Every_cell_puts_the_box_at_the_margin(int w, int h, TextGridRow row, TextGridColumn col, double x, double y)
    {
        var (px, py) = TextPlacement.Position(new FrameSize(w, h), Plain(), VisualProperties.Default, 300, 100, row, col);
        Assert.Equal(x, px, 9);
        Assert.Equal(y, py, 9);
    }

    [Fact]
    public void The_background_grows_the_box_by_its_padding_and_wins_over_the_outline()
    {
        var text = Plain(40) with
        {
            Background = TextBackground.Default with { IsEnabled = true }, Outline = TextOutline.Default with { IsEnabled = true, Thickness = 0.2 }
        };
        var (x, y) = TextPlacement.Position(new FrameSize(1080, 1920), text, VisualProperties.Default, 300, 100, TextGridRow.Bottom, TextGridColumn.Left);
        var e = 0.25 * 40;
        Assert.Equal(-540 + 86.4 + (300 + 2 * e) / 2, x, 9);
        Assert.Equal(960 - 86.4 - (100 + 2 * e) / 2, y, 9);
    }

    [Fact]
    public void The_outline_alone_grows_the_box_by_its_width()
    {
        var text = Plain(40) with { Outline = TextOutline.Default with { IsEnabled = true, Thickness = 0.1 } };
        var (_, y) = TextPlacement.Position(new FrameSize(1080, 1920), text, VisualProperties.Default, 300, 100, TextGridRow.Top, TextGridColumn.Centre);
        Assert.Equal(-960 + 86.4 + (100 + 2 * 4) / 2.0, y, 9);
    }

    [Fact]
    public void Scale_and_rotation_use_the_bounds_of_the_turned_box()
    {
        var visual = VisualProperties.Default with { Scale = 2, RotationDegrees = 90 };
        var (x, y) = TextPlacement.Position(new FrameSize(1920, 1080), Plain(), visual, 300, 100, TextGridRow.Top, TextGridColumn.Right);
        Assert.Equal(960 - 86.4 - 100, x, 6);           // turned: 200 × 600 at scale 2
        Assert.Equal(-540 + 86.4 + 300, y, 6);
    }

    [Fact]
    public void A_plain_text_reaches_nothing_past_its_box() => Assert.Equal(0, TextPlacement.DrawnExtent(Plain()));
}
