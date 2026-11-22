using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>D074 amendment 1: one route of «Итоги» in its Inspector — the attempts typed here are stated on the route's
/// «Боулдер» plates (empty: counted from the video again); a flash is always one attempt.</summary>
public sealed partial class SummaryAttemptItem : ObservableObject
{
    private readonly Action<SummaryAttemptItem, string> _changed;
    internal bool Syncing;

    internal SummaryAttemptItem(SummaryAttemptRow row, Action<SummaryAttemptItem, string> changed)
    {
        _changed = changed;
        Row = row;
        Syncing = true;
        AttemptsText = row.Stated ? row.Attempts.ToString(CultureInfo.InvariantCulture) : "";
        Syncing = false;
    }

    public SummaryAttemptRow Row { get; private set; }

    public string Label => string.Join(" · ", new[] { Row.Route, Row.Grade }.Where(s => s.Length > 0)) is { Length: > 0 } name ? name : "—";

    public string ResultText => Row.Result switch
    {
        BoulderStatus.Top => TemplateRules.TopWord,
        BoulderStatus.Flash => TemplateRules.FlashWord,
        BoulderStatus.Attempt => Strings.TemplateStatus_Attempt,
        _ => "—"
    };

    public bool IsEditable => Row.Result != BoulderStatus.Flash;

    /// <summary>The attempts the board counts from the video, shown when nothing is stated.</summary>
    public string Counted => string.Format(Strings.Inspector_SummaryAttemptsCounted, Row.Result == BoulderStatus.Flash ? 1 : Row.Counted);

    [ObservableProperty] private string _attemptsText = "";

    partial void OnAttemptsTextChanged(string value)
    {
        if (!Syncing) _changed(this, value);
    }

    /// <summary>The same route after a change of the project: the numbers follow, the text the user is typing is not replaced
    /// while it says the same.</summary>
    internal void Update(SummaryAttemptRow row)
    {
        Row = row;
        var text = row.Stated ? row.Attempts.ToString(CultureInfo.InvariantCulture) : "";
        Syncing = true;
        if (!string.Equals(AttemptsText.Trim(), text, StringComparison.Ordinal)) AttemptsText = text;
        Syncing = false;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(Counted));
    }
}

public sealed partial class InspectorViewModel
{
    /// <summary>D074 amendment 1: the routes of the selected «Итоги», each with its attempts.</summary>
    public ObservableCollection<SummaryAttemptItem> SummaryAttempts { get; } = new();

    [ObservableProperty] private bool _hasSummaryAttempts;

    /// <summary>Fills the list from the selection (also after every change of the project: the selection is shown again).</summary>
    private void RefreshSummaryAttempts(TimelineClipSelection selection)
    {
        var rows = selection.Clip is TemplateClip { Kind: Core.Entities.TemplateKind.Summary } summary && selection.Sequence is { } sequence
            ? SummaryBoard.AttemptRows(sequence, summary.TimelineStart, selection.Rate)
            : Array.Empty<SummaryAttemptRow>();
        static string Key(SummaryAttemptRow r) => string.Join(",", r.Plates);
        if (rows.Count == SummaryAttempts.Count && rows.Select(Key).SequenceEqual(SummaryAttempts.Select(i => Key(i.Row))))
        {
            for (var i = 0; i < rows.Count; i++) SummaryAttempts[i].Update(rows[i]);
        }
        else
        {
            SummaryAttempts.Clear();
            foreach (var row in rows) SummaryAttempts.Add(new SummaryAttemptItem(row, OnSummaryAttemptsTyped));
        }
        HasSummaryAttempts = SummaryAttempts.Count > 0;
    }

    private void OnSummaryAttemptsTyped(SummaryAttemptItem item, string value)
    {
        if (RejectWhileLocked() || !item.IsEditable) return;
        var text = value.Trim();
        if (text.Length == 0) Apply(_edit.SetRouteAttempts(item.Row.Plates, null));
        else if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
                 n is >= TemplateRules.MinAttempts and <= TemplateRules.MaxAttempts)
            Apply(_edit.SetRouteAttempts(item.Row.Plates, n));
    }
}
