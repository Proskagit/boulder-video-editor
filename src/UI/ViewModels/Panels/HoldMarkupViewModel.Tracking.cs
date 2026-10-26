using System.Globalization;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Hold tracking (R2 → E) in the Inspector's "Holds" section: the clip's state — analysing, fixed to the wall (with the
/// share of frames whose contours are hidden because the camera's position there is not reliably known), failed — and the
/// manual correction when the tracking is wrong: "Follow the camera" off leaves the contours where they were marked, in the
/// Preview and the export alike, for this session; "Retry" analyses a failed clip again. Neither is a project edit (no Undo
/// step, not saved; <c>project.json</c> v9 unchanged).
/// </summary>
public sealed partial class HoldMarkupViewModel
{
    private CameraMotionCoordinator? _cameraMotion;

    /// <summary>The clip has holds and tracking is available: the block is shown.</summary>
    [ObservableProperty] private bool _showsTracking;

    [ObservableProperty] private bool _followsCamera = true;
    [ObservableProperty] private string _trackingStatus = string.Empty;
    [ObservableProperty] private bool _isTrackingFailed;

    private void InitTracking(CameraMotionCoordinator? cameraMotion)
    {
        _cameraMotion = cameraMotion;
        if (_cameraMotion is not null) _cameraMotion.StatusChanged += (_, _) => OnUi(RefreshTracking);
    }

    /// <summary>Reads the selected clip's state from the coordinator (called with the rest of the section).</summary>
    private void RefreshTracking()
    {
        var status = _clip is not null && _cameraMotion is not null
            ? _cameraMotion.StatusOf(_clip.Id)
            : new CameraMotionStatus(CameraMotionState.None);
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            ShowsTracking = status.State != CameraMotionState.None;
            FollowsCamera = status.State != CameraMotionState.Off;
            IsTrackingFailed = status.State == CameraMotionState.Failed;
            TrackingStatus = status.State switch
            {
                CameraMotionState.Analysing => Strings.Holds_TrackingAnalysing,
                CameraMotionState.Tracked when status.HiddenFraction > 0 => string.Format(CultureInfo.CurrentCulture,
                    Strings.Holds_TrackingTrackedPartly, Math.Max(1, (int)Math.Round(status.HiddenFraction * 100))),
                CameraMotionState.Tracked => Strings.Holds_TrackingTracked,
                CameraMotionState.Failed => Strings.Holds_TrackingFailed,
                CameraMotionState.Off => Strings.Holds_TrackingOff,
                _ => string.Empty
            };
        }
        finally
        {
            _syncing = wasSyncing;
        }
        RetryTrackingCommand.NotifyCanExecuteChanged();
    }

    partial void OnFollowsCameraChanged(bool value)
    {
        if (_syncing || _clip is null || _cameraMotion is null) return;
        _cameraMotion.SetFollowsCamera(_clip.Id, value);
    }

    private bool CanRetryTracking() => IsTrackingFailed && _clip is not null && _cameraMotion is not null;

    [RelayCommand(CanExecute = nameof(CanRetryTracking))]
    private void RetryTracking()
    {
        if (_clip is not null) _cameraMotion?.Retry(_clip.Id);
    }
}
