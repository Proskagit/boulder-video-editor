using System.Windows.Input;
using AiVideoEditor.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// The editor's keyboard shortcuts (the main window's key handling, D024 Step 9.6): which command a key with its exact
/// modifiers runs, and when a key is consumed. Separate from the window so the routing is tested without one.
/// <list type="bullet">
/// <item>Never while a text input has focus (or sent the key): a <see cref="TextBox"/> — also the one inside a
/// <see cref="NumericUpDown"/> — doesn't mark plain letter keys as handled on KeyDown (text arrives via TextInput), so
/// "S" would otherwise split the timeline while the user types.</item>
/// <item>A known shortcut is consumed even when its command is unavailable right now (e.g. editing during an export,
/// <c>EditingLock</c>): it does nothing rather than reaching another control.</item>
/// <item>Modifiers must match exactly: Shift+S is not S, Ctrl+Shift+S is not Ctrl+S.</item>
/// </list>
/// </summary>
public static class ShortcutRouter
{
    /// <summary>Handles a key the window received (not handled by a focused control): runs its command if it may run.
    /// Returns whether the key was consumed.</summary>
    public static bool Handle(MainWindowViewModel vm, Key key, KeyModifiers modifiers, object? source, object? focused)
    {
        if (IsTextInput(source) || IsTextInput(focused)) return false;

        var command = CommandFor(vm, key, modifiers);
        if (command is null) return false;
        if (command.CanExecute(null))
            command.Execute(null);
        return true;
    }

    /// <summary>Whether <paramref name="element"/> takes typed text, so shortcuts must stay off.</summary>
    public static bool IsTextInput(object? element) => element is TextBox;

    /// <summary>The command bound to <paramref name="key"/> with exactly <paramref name="modifiers"/>, or null.</summary>
    public static ICommand? CommandFor(MainWindowViewModel vm, Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers == KeyModifiers.Control;
        var ctrlShift = modifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        var shift = modifiers == KeyModifiers.Shift;
        var none = modifiers == KeyModifiers.None;
        var timeline = vm.Timeline;

        return key switch
        {
            Key.N when ctrl => vm.Toolbar.NewProjectCommand,
            Key.O when ctrl => vm.Toolbar.OpenCommand,
            Key.S when ctrl => vm.Toolbar.SaveCommand,
            Key.S when ctrlShift => vm.Toolbar.SaveAsCommand,
            Key.Z when ctrl => vm.Toolbar.UndoCommand,
            Key.Y when ctrl => vm.Toolbar.RedoCommand,
            Key.Z when ctrlShift => vm.Toolbar.RedoCommand,
            Key.Delete or Key.Back when none => timeline.DeleteSelectedCommand,
            Key.S when none => timeline.SplitAtPlayheadCommand,
            Key.N when none => timeline.ToggleSnappingCommand,
            Key.Left when none => timeline.StepBackwardCommand,
            Key.Right when none => timeline.StepForwardCommand,
            Key.Left when shift => timeline.StepBackwardSecondCommand,
            Key.Right when shift => timeline.StepForwardSecondCommand,
            Key.Space when none => vm.Preview.PlayPauseCommand,
            // J / K / L (D024 Step 9.6, PO-H1 / PO-H2): forward playback at 1× only (D010 / D011) — J steps back one
            // second keeping the playback state (a seek never changes it), K pauses, L plays.
            Key.J when none => timeline.StepBackwardSecondCommand,
            Key.K when none => vm.Preview.PauseCommand,
            Key.L when none => vm.Preview.PlayCommand,
            Key.L when ctrl => vm.Preview.ToggleLoopCommand,                  // PO-H3
            Key.Home when none => timeline.GoToStartCommand,
            Key.End when none => timeline.GoToEndCommand,
            Key.OemPlus or Key.Add when ctrl => timeline.ZoomInCommand,
            Key.OemMinus or Key.Subtract when ctrl => timeline.ZoomOutCommand,
            // Existing commands without a shortcut until Step 9.6 (PO-H4). "\" is OemPipe on US layouts and
            // OemBackslash (the key next to the left Shift) on ISO ones.
            Key.I when ctrl => vm.Toolbar.ImportMediaCommand,
            Key.E when ctrl => vm.Toolbar.ExportCommand,
            Key.OemPipe or Key.OemBackslash when none => timeline.ZoomToFitCommand,
            // Phase 12 Step 12.6 (D027 §5): the clipboard of the timeline — never while typing (the text box keeps its
            // own Ctrl+C / Ctrl+V, above).
            Key.C when ctrl => timeline.CopyCommand,
            Key.V when ctrl => timeline.PasteCommand,
            Key.D when ctrl => timeline.DuplicateCommand,
            _ => null
        };
    }
}
