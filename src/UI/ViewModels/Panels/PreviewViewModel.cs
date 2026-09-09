using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Central preview transport. Play/Pause here only flips a boolean — there is no
/// decoder, no timer, no frame source yet. Real playback (via <c>IPlaybackService</c>,
/// driven by the FFmpeg-backed engine) arrives in Phase 4, wired to the timeline
/// playhead. The mock frame step size below (1/30s) stands in for a real FPS from
/// <c>ProjectSettings</c> until a project actually exists (Phase 5).
/// </summary>
public sealed partial class PreviewViewModel : ViewModelBase
{
    private static readonly MediaTime MockFrameStep = MediaTime.FromSeconds(1.0 / 30.0);

    private readonly ILogger<PreviewViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentTimeDisplay))]
    private MediaTime _currentTime = MediaTime.FromSeconds(12);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private MediaTime _duration = MediaTime.FromSeconds(95);

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";
    public string CurrentTimeDisplay => TimeFormat.ToShortString(CurrentTime);
    public string DurationDisplay => TimeFormat.ToShortString(Duration);

    public PreviewViewModel(ILogger<PreviewViewModel> logger)
    {
        _logger = logger;
    }

    [RelayCommand]
    private void PlayPause()
    {
        IsPlaying = !IsPlaying;
        _logger.LogInformation("Preview {State} (mock — no decoder attached yet).", IsPlaying ? "playing" : "paused");
    }

    [RelayCommand]
    private void Stop()
    {
        IsPlaying = false;
        CurrentTime = MediaTime.Zero;
    }

    [RelayCommand]
    private void PreviousFrame()
    {
        var candidate = CurrentTime - MockFrameStep;
        CurrentTime = candidate < MediaTime.Zero ? MediaTime.Zero : candidate;
    }

    [RelayCommand]
    private void NextFrame()
    {
        var candidate = CurrentTime + MockFrameStep;
        CurrentTime = candidate > Duration ? Duration : candidate;
    }
}
