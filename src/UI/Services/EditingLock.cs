using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.Services;

/// <summary>
/// Shared "the project must not change now" state (Phase 8 Step 7, D023: editing is blocked while exporting).
/// A singleton the panels consult in their command <c>CanExecute</c> methods and edit entry points, so the
/// commands that could change the project, the timeline or the media list — and with them what an export's
/// snapshot was taken from — are disabled, while viewing (playhead, zoom, selection, playback) stays available.
/// It blocks the UI only; the model services and undo/redo behave as before.
/// </summary>
public sealed partial class EditingLock : ObservableObject
{
    /// <summary>True while an export runs (from the job's creation until <c>ExportAsync</c> has finished).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAllowed))]
    private bool _isLocked;

    public bool IsEditingAllowed => !IsLocked;

    /// <summary>Locks until the returned scope is disposed. Not re-entrant: only one holder at a time.</summary>
    public IDisposable Acquire()
    {
        if (IsLocked) throw new InvalidOperationException("Editing is already locked.");
        IsLocked = true;
        return new Scope(this);
    }

    private sealed class Scope(EditingLock owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.IsLocked = false;
        }
    }
}
