using System.Globalization;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Hold tracking (R2 → E) in the Inspector's "Holds" section: the clip's state — analysing, fixed to the wall, partly or
/// mostly unreliable (with the share of frames whose contours are hidden because the camera's position there is not
/// reliably known), failed (the contours do not follow the camera) — and the manual correction when the tracking is wrong:
/// "Follow the camera" off leaves the contours where they were marked, in the Preview and the export alike. That choice is
/// the clip's, saved with the project (K3, <c>project.json</c> v14) — one Undo step through
/// <see cref="Core.Interfaces.ITimelineEditService.SetHoldFollowsCamera"/>. "Retry" analyses a failed clip again (no edit).
/// </summary>
public sealed partial class HoldMarkupViewModel
{
    private CameraMotionCoordinator? _cameraMotion;

    /// <summary>The clip has holds and tracking is available: the block is shown.</summary>
    [ObservableProperty] private bool _showsTracking;

    [ObservableProperty] private bool _followsCamera = true;
    [ObservableProperty] private string _trackingStatus = string.Empty;
    [ObservableProperty] private bool _isTrackingFailed;

    /// <summary>The contours do not get a correct compensation on every frame (K2): the status is shown as a warning.</summary>
    [ObservableProperty] private bool _hasTrackingProblem;

    private void InitTracking(CameraMotionCoordinator? cameraMotion)
    {
        _cameraMotion = cameraMotion;
        if (_cameraMotion is not null) _cameraMotion.StatusChanged += (_, _) => OnUi(RefreshTracking);
    }

    /// <summary>Reads the selected clip's state from the coordinator (called with the rest of the section).</summary>
    private void RefreshTracking()
    {
        var status = _clip is not null && _routeId is { } route && _cameraMotion is not null
            ? _cameraMotion.StatusOf(new Core.Motion.RouteKey(_clip.Id, route))
            : new CameraMotionStatus(CameraMotionState.None);
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            ShowsTracking = status.State != CameraMotionState.None;
            FollowsCamera = status.State != CameraMotionState.Off;
            IsTrackingFailed = status.State == CameraMotionState.Failed;
            HasTrackingProblem = status.IsProblem;
            var percent = Math.Max(1, (int)Math.Round(status.HiddenFraction * 100));
            TrackingStatus = status.State switch
            {
                CameraMotionState.Analysing => Strings.Holds_TrackingAnalysing,
                CameraMotionState.Tracked => Strings.Holds_TrackingTracked,
                CameraMotionState.PartlyUnreliable => string.Format(CultureInfo.CurrentCulture, Strings.Holds_TrackingTrackedPartly, percent),
                CameraMotionState.Unreliable => string.Format(CultureInfo.CurrentCulture, Strings.Holds_TrackingUnreliable, percent),
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
        if (_syncing || _clip is null) return;
        var result = _edit.SetHoldFollowsCamera(_clip.Id, value, _routeId);
        if (!result.Success) _status.Report(result.Message ?? Strings.Holds_OutlineNotChanged);
        RefreshTracking();
    }

    private bool CanRetryTracking() => IsTrackingFailed && _clip is not null && _routeId is not null && _cameraMotion is not null;

    [RelayCommand(CanExecute = nameof(CanRetryTracking))]
    private void RetryTracking()
    {
        if (_clip is not null && _routeId is { } route) _cameraMotion?.Retry(new Core.Motion.RouteKey(_clip.Id, route));
    }
}
