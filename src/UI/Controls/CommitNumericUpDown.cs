using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace AiVideoEditor.UI.Controls;

/// <summary>
/// A <see cref="NumericUpDown"/> whose typed text becomes its value only when the input is committed — Enter or the
/// field losing the focus (the base control's own commit) — not on every keystroke. Avalonia's control parses the text
/// after each key, so typing "99" into a field limited to 51 first applied 9 as an edit and then refused 99, leaving the
/// model at 9; with a fraction ("2,5") the "2" stayed. Every value it sends is now one the user finished typing; the
/// spinner arrows and the arrow keys still change the value at once. Styled as a plain NumericUpDown.
/// <para>
/// With a <see cref="StepCommand"/> a spin (the spinner arrows, the arrow keys, the wheel) is not a value change: the
/// command gets the direction (+1 / −1) and decides the next value; a typed value is always just a value. The field of a
/// transition's seconds steps on the 0.1 s grid this way (D044 §5), and typing 0.85 is never mistaken for a step.
/// </para>
/// </summary>
public class CommitNumericUpDown : NumericUpDown
{
    public static readonly StyledProperty<ICommand?> StepCommandProperty =
        AvaloniaProperty.Register<CommitNumericUpDown, ICommand?>(nameof(StepCommand));

    /// <summary>When set, a spin executes it with +1 or −1 instead of adding <see cref="NumericUpDown.Increment"/>.</summary>
    public ICommand? StepCommand
    {
        get => GetValue(StepCommandProperty);
        set => SetValue(StepCommandProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(NumericUpDown);

    /// <summary>True while the user is typing in the field (it has the keyboard focus): the text is not parsed yet.</summary>
    internal bool IsTyping => TypingOverride ?? IsKeyboardFocusWithin;

    /// <summary>Tests only: replaces the focus check (a headless control never has the keyboard focus).</summary>
    internal bool? TypingOverride { get; set; }

    protected override void OnSpin(SpinEventArgs e)
    {
        if (!Step(e.Direction)) base.OnSpin(e);
    }

    /// <summary>A spin in <paramref name="direction"/> through <see cref="StepCommand"/>; false when there is none.</summary>
    internal bool Step(SpinDirection direction)
    {
        if (StepCommand is not { } command) return false;
        var step = direction == SpinDirection.Increase ? 1 : -1;
        if (command.CanExecute(step)) command.Execute(step);
        return true;
    }

    /// <summary>D068 amendment 1 (as CapCut): the two spinner buttons are one narrow column at the field's right edge — the
    /// upper half steps up, the lower half down — instead of two buttons side by side, so the field is compact.</summary>
    private bool _stacked;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _stacked = false;
        EffectiveViewportChanged -= OnShown;
        LayoutUpdated -= OnFirstLayout;
        LayoutUpdated += OnFirstLayout;
    }

    // The spinner's own template is applied during the first layout: the buttons are found once they exist.
    private void OnFirstLayout(object? sender, EventArgs e)
    {
        // A field in a collapsed section has no height yet: it waits for being shown (EffectiveViewportChanged), it does not
        // walk its tree on every layout pass of the window.
        LayoutUpdated -= OnFirstLayout;
        if (_stacked) return;
        _stacked = StackButtons(this);
        if (!_stacked) EffectiveViewportChanged += OnShown;
    }

    private void OnShown(object? sender, Avalonia.Layout.EffectiveViewportChangedEventArgs e)
    {
        if (Bounds.Height <= 0) return;
        EffectiveViewportChanged -= OnShown;
        _stacked = StackButtons(this);
    }

    /// <summary>D069 §4: how much smaller a stacked button's arrow glyph is than Fluent's, and its width when Fluent sets
    /// none.</summary>
    internal const double ArrowGlyphShrink = 1.5;
    internal const double ArrowGlyphWidth = 16 / ArrowGlyphShrink;

    internal static bool StackButtons(Visual field)
    {
        var buttons = field.GetVisualDescendants().OfType<RepeatButton>().ToList();
        if (buttons.Count != 2 || buttons[0].GetVisualParent() is not Panel panel || buttons[1].GetVisualParent() != panel) return false;
        if (field.Bounds.Height <= 0) return false;
        var half = Math.Max(8, Math.Floor((field.Bounds.Height - 2) / 2));
        switch (panel)
        {
            case StackPanel stack: stack.Orientation = Orientation.Vertical; break;
            case UniformGrid grid: grid.Rows = 2; grid.Columns = 1; break;
            case Grid grid:
                grid.ColumnDefinitions.Clear();
                grid.RowDefinitions = new RowDefinitions("*,*");
                for (var i = 0; i < 2; i++) { Grid.SetColumn(buttons[i], 0); Grid.SetRow(buttons[i], i); }
                break;
            default: return false;
        }
        panel.VerticalAlignment = VerticalAlignment.Center;
        foreach (var button in buttons)
        {
            button.Width = 16;
            button.MinWidth = 0;
            button.MinHeight = 0;
            button.Height = half;
            button.Padding = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Stretch;
            // D069 §4: Fluent's arrow glyph is sized for its wide button — in the 16 px column it filled the button and
            // stuck out of it; about 1.5 times smaller, it fits.
            // D069 amendment 1: Fluent draws the arrow as a PathIcon 16 × 8 — the button's content, made before the button's own
            // template shows it — whose Path is stretched to fill it: the icon itself gets the smaller size.
            foreach (var glyph in Glyphs(button))
            {
                glyph.Width = ArrowGlyphWidth;
                glyph.Height = ArrowGlyphWidth / 2;
                glyph.MinWidth = 0;   // a minimum of the theme would keep the old size
                glyph.MinHeight = 0;
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                if (glyph is Avalonia.Controls.Shapes.Shape shape) shape.Stretch = Avalonia.Media.Stretch.Uniform;
            }
        }
        return true;
    }

    /// <summary>The arrow glyph of a spinner button: its content when that is an icon or a shape, else the icons / bare shapes
    /// it shows.</summary>
    private static IEnumerable<Control> Glyphs(RepeatButton button)
    {
        if (button.Content is PathIcon or Avalonia.Controls.Shapes.Shape) return new[] { (Control)button.Content! };
        return button.GetVisualDescendants()
            .Where(v => v is PathIcon || v is Avalonia.Controls.Shapes.Shape && v.FindAncestorOfType<PathIcon>() is null)
            .OfType<Control>().ToList();
    }

    protected override void OnTextChanged(string? oldValue, string? newValue)
    {
        if (IsTyping) return;    // committed by Enter / focus loss (NumericUpDown.CommitInput)
        base.OnTextChanged(oldValue, newValue);
    }
}
