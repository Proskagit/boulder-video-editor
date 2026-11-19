using AiVideoEditor.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace AiVideoEditor.Rendering.Tests;

/// <summary>D069 §4 / amendment 1: in Fluent's real template the stacked spinner's arrow glyphs (a PathIcon each) fit inside
/// their buttons — the glyph about 1.5 times smaller than Fluent's 16 × 8.</summary>
[Collection(AvaloniaCollection.Name)]
public sealed class SpinnerArrowTests
{
    [Fact]
    public async Task The_arrows_fit_their_buttons()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // The theme for this test only (the other tests draw without it).
            var theme = new Avalonia.Themes.Fluent.FluentTheme();
            Application.Current!.Styles.Add(theme);
            try
            {
            var field = new CommitNumericUpDown { Width = 60, Height = 32, Value = 100 };
            var host = new Window { Content = field, Width = 200, Height = 60, SizeToContent = SizeToContent.Manual };
            host.ApplyStyling();
            host.Measure(new Size(200, 60));
            host.Arrange(new Rect(0, 0, 200, 60));
            Assert.True(CommitNumericUpDown.StackButtons(field));
            // The window is never shown (no layout manager runs): every element is measured again by hand.
            foreach (var element in field.GetVisualDescendants().OfType<Layoutable>().Prepend(field)) element.InvalidateMeasure();
            host.InvalidateMeasure();
            host.Measure(new Size(200, 60));
            host.Arrange(new Rect(0, 0, 200, 60));

            var buttons = field.GetVisualDescendants().OfType<RepeatButton>().ToList();
            Assert.Equal(2, buttons.Count);
            foreach (var button in buttons)
            {
                var glyph = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
                // Measured in Fluent's template (the window is never shown, so the arrange is not re-run): the glyph's size and
                // the button's column (16 px wide, half the field high) — the arrow fits it now; Fluent's own is 16 × 8.
                Assert.InRange(glyph.DesiredSize.Width, 10, 12);
                Assert.True(glyph.DesiredSize.Width <= button.Width && glyph.DesiredSize.Height <= button.Height,
                    $"glyph {glyph.DesiredSize} in button {button.Width} x {button.Height}");
            }
            host.Close();
            }
            finally
            {
                Application.Current!.Styles.Remove(theme);
            }
        });
    }
}
