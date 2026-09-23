using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Central preview transport. Shows the real timeline playhead and sequence duration
/// (pushed in by MainWindowViewModel via <see cref="SetPosition"/>); the frame-step and
/// stop buttons move the timeline playhead through events, so the timeline stays the
/// single owner of the playhead. There is no decoder yet: Play reports that playback
/// arrives in Phase 5 instead of pretending to play.
/// </summary>
public sealed partial class PreviewViewModel : ViewModelBase
{
    private readonly StatusService _status;
    private readonly ILogger<PreviewViewModel> _logger;

    [ObservableProperty] private string _currentTimeDisplay = "00:00:00:00";
    [ObservableProperty] private string _durationDisplay = "00:00:00:00";

    public string PlayPauseLabel => "Play";

    /// <summary>Requests a playhead step by this many frames (negative = back).</summary>
    public event EventHandler<long>? FrameStepRequested;

    /// <summary>Requests the playhead to jump to the start of the timeline.</summary>
    public event EventHandler? GoToStartRequested;

    public PreviewViewModel(StatusService status, ILogger<PreviewViewModel> logger)
    {
        _status = status;
        _logger = logger;
    }

    public void SetPosition(MediaTime playhead, MediaTime duration, FrameRate rate)
    {
        CurrentTimeDisplay = TimeFormat.ToTimecode(playhead, rate);
        DurationDisplay = TimeFormat.ToTimecode(duration, rate);
    }

    [RelayCommand]
    private void PlayPause()
    {
        _status.Report("Playback isn't implemented yet (arrives in Phase 5).");
        _logger.LogDebug("Play pressed; no playback engine yet.");
    }

    [RelayCommand]
    private void Stop() => GoToStartRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void PreviousFrame() => FrameStepRequested?.Invoke(this, -1);

    [RelayCommand]
    private void NextFrame() => FrameStepRequested?.Invoke(this, 1);
}
