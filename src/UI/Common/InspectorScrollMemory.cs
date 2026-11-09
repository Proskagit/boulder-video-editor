using AiVideoEditor.Core.Interfaces;

namespace AiVideoEditor.UI.Common;

/// <summary>
/// D057 §3.1: the Inspector's scroll position per kind of item — selecting another video clip keeps the place the user
/// scrolled to among the video clips' sections; another kind of item has its own. Kept in the user's layout.
/// </summary>
public sealed class InspectorScrollMemory(IUiStateService store)
{
    private bool _restoring;

    /// <summary>The kind shown now.</summary>
    public string? Key { get; private set; }

    /// <summary>The user scrolled (or the content moved the offset) while <see cref="Key"/> is shown.</summary>
    public void Scrolled(double offset)
    {
        if (_restoring || Key is null || double.IsNaN(offset) || offset < 0) return;
        var key = Key;
        var rounded = Math.Round(offset, 1);
        store.Update(s => s.InspectorScroll.TryGetValue(key, out var old) && old == rounded
            ? s
            : s with { InspectorScroll = s.InspectorScroll.SetItem(key, rounded) });
    }

    /// <summary>A selection of kind <paramref name="key"/> is shown: the offset to restore for it (0 the first time). Until
    /// <see cref="Restored"/> the offsets the new content's layout produces are not taken for the user's scroll.</summary>
    public double Show(string key)
    {
        Key = key;
        _restoring = true;
        return store.Current.InspectorScroll.TryGetValue(key, out var offset) ? offset : 0;
    }

    /// <summary>The view has put the offset back: from now on a scroll is the user's.</summary>
    public void Restored() => _restoring = false;
}
