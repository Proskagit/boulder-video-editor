using System.Collections.Immutable;

namespace AiVideoEditor.Core.Interfaces;

/// <summary>The main window's place and size (D057 §3.4), in device-independent pixels; the position in screen pixels.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>
/// The user's working layout (D057 §3.1, §3.4) — view state of one Windows user, never a project's: the window, the panels'
/// sizes, the Inspector's collapsed sections (by their stable ids) and its scroll position per kind of item.
/// </summary>
public sealed record UiState
{
    public WindowPlacement? Window { get; init; }

    public double? MediaBrowserWidth { get; init; }

    public double? InspectorWidth { get; init; }

    public double? TimelineHeight { get; init; }

    /// <summary>The Inspector's sections the user collapsed or expanded, by id — true: collapsed. A section not named here
    /// has its default.</summary>
    public ImmutableSortedDictionary<string, bool> InspectorSections { get; init; } = ImmutableSortedDictionary<string, bool>.Empty;

    /// <summary>The Inspector's vertical scroll position per kind of selected item («video», «audio», …).</summary>
    public ImmutableSortedDictionary<string, double> InspectorScroll { get; init; } = ImmutableSortedDictionary<string, double>.Empty;

    public bool Equals(UiState? other) =>
        other is not null && Window == other.Window && MediaBrowserWidth == other.MediaBrowserWidth &&
        InspectorWidth == other.InspectorWidth && TimelineHeight == other.TimelineHeight &&
        InspectorSections.SequenceEqual(other.InspectorSections) && InspectorScroll.SequenceEqual(other.InspectorScroll);

    public override int GetHashCode() => HashCode.Combine(Window, MediaBrowserWidth, InspectorWidth, TimelineHeight,
        InspectorSections.Count, InspectorScroll.Count);
}

/// <summary>The working layout (D057 §3.1, §3.4): read at start, changed by the window and the Inspector.</summary>
public interface IUiStateService
{
    UiState Current { get; }

    /// <summary>Applies <paramref name="change"/>; a change that leaves the state equal does nothing. Saved to the file
    /// soon after (a burst of changes is one write) and on <see cref="Flush"/>; a failed save is logged, never thrown.</summary>
    void Update(Func<UiState, UiState> change);

    /// <summary>Writes a pending change now (the window closing).</summary>
    void Flush();
}

/// <summary>A layout in memory only — the default for code and tests without a file.</summary>
public sealed class InMemoryUiState(UiState? initial = null) : IUiStateService
{
    public UiState Current { get; private set; } = initial ?? new UiState();

    public void Update(Func<UiState, UiState> change) => Current = change(Current);

    public void Flush() { }
}
