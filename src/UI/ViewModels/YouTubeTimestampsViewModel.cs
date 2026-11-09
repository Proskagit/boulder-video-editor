using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels;

/// <summary>
/// D057 §3.9: the YouTube timestamps of the timeline — a plain text to copy into a video's description
/// (<see cref="YouTubeTimestamps"/>). With an In / Out range the user chooses between the range (its times from In, the
/// routes outside it left out, as a range export) and the whole sequence.
/// </summary>
public sealed partial class YouTubeTimestampsViewModel : ViewModelBase
{
    private readonly Sequence _sequence;
    private readonly FrameRate _rate;
    private readonly (MediaTime Start, MediaTime End)? _range;

    public YouTubeTimestampsViewModel(Sequence sequence, FrameRate rate, (MediaTime Start, MediaTime End)? range = null)
    {
        _sequence = sequence;
        _rate = rate;
        _range = range;
        _useRange = range is not null;
        Refresh();
    }

    public bool HasRange => _range is not null;

    [ObservableProperty] private bool _useRange;

    [ObservableProperty] private string _text = "";

    partial void OnUseRangeChanged(bool value) => Refresh();

    private void Refresh() => Text = UseRange && _range is { } r
        ? YouTubeTimestamps.Text(_sequence, _rate, r.Start, r.End)
        : YouTubeTimestamps.Text(_sequence, _rate);

    /// <summary>Raised by «Копировать» with the text; the view puts it on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, Text);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
