using System.Collections.Immutable;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// D064 in the Inspector's «Звук»: a video clip's sound-only fades (whole frames — the exact values of the timeline's handles),
/// the volume points (how many; «Удалить точки»), the clean-up — «Шумоподавление» and «Улучшение речи», each a strength 0–100
/// (0 = off) — with the state of its processing (progress, «Остановить», «Повторить», the reason of a failure) and the
/// comparison with the original sound. Each change of the clip is one undo step (a slider's drag merges, D017); the processing
/// is never part of the project.
/// </summary>
public sealed partial class InspectorViewModel
{
    private AudioProcessingCoordinator? _audioProcessing;

    /// <summary>The audio processing (set by the main window); null: the clean-up's state is not shown.</summary>
    public AudioProcessingCoordinator? AudioProcessing
    {
        get => _audioProcessing;
        set
        {
            if (_audioProcessing is not null) _audioProcessing.StatusChanged -= OnAudioProcessingChanged;
            _audioProcessing = value;
            if (_audioProcessing is not null) _audioProcessing.StatusChanged += OnAudioProcessingChanged;
            RefreshCleanupStatus();
        }
    }

    /// <summary>A video clip with sound: its sound-only fades are shown.</summary>
    [ObservableProperty] private bool _hasSoundFades;
    [ObservableProperty] private decimal? _soundFadeInFrames;
    [ObservableProperty] private decimal? _soundFadeOutFrames;

    [ObservableProperty] private decimal? _noiseReduction = 0;
    [ObservableProperty] private decimal? _voiceEnhancement = 0;

    [ObservableProperty] private string _volumePointsText = string.Empty;
    [ObservableProperty] private bool _hasVolumePoints;

    [ObservableProperty] private string _cleanupStatus = string.Empty;
    [ObservableProperty] private bool _hasCleanupStatus;
    [ObservableProperty] private bool _canCancelCleanup;
    [ObservableProperty] private bool _canRetryCleanup;

    /// <summary>D064 §5 «Исходный звук»: the Preview plays every clip's original sound (never saved, never exported).</summary>
    public bool OriginalSound
    {
        get => _audioProcessing?.OriginalSound ?? false;
        set
        {
            if (_audioProcessing is null || _audioProcessing.OriginalSound == value) return;
            _audioProcessing.OriginalSound = value;
            OnPropertyChanged();
        }
    }

    private void OnAudioProcessingChanged(object? sender, EventArgs e)
    {
        RefreshCleanupStatus();
        OnPropertyChanged(nameof(OriginalSound));
    }

    /// <summary>Called by <see cref="SyncFromModel"/>: the audio fields of the shown clip.</summary>
    private void SyncAudioExtras(Clip clip)
    {
        if (AudioProperties.Of(clip) is not { } audio)
        {
            HasSoundFades = false;
            HasVolumePoints = false;
            return;
        }
        HasSoundFades = clip is VideoClip;
        SoundFadeInFrames = audio.SoundFadeIn.ToNearestFrame(_rate);
        SoundFadeOutFrames = audio.SoundFadeOut.ToNearestFrame(_rate);
        NoiseReduction = audio.Cleanup?.NoiseReduction ?? 0;
        VoiceEnhancement = audio.Cleanup?.VoiceEnhancement ?? 0;
        HasVolumePoints = audio.Volumes is not null;
        VolumePointsText = audio.Volumes is { } points
            ? Core.Localization.Plural.Format(points.Count, Strings.Plural_VolumePoints)
            : Strings.Inspector_NoVolumePoints;
        RefreshCleanupStatus();
    }

    // --- D064 amendment 1: the volume field with points ----------------------------------------------------------------

    /// <summary>The playhead's frame on the clip and its source time (clamped to the clip), or null without a playhead.</summary>
    private (long Frame, MediaTime Source)? VolumeFrame(MediaBackedClip clip)
    {
        if (Playhead is null) return null;
        var first = clip.TimelineStart.ToFrameFloor(_rate);
        var last = Math.Max(first, clip.TimelineEnd.ToFrameFloor(_rate) - 1);
        var frame = Math.Clamp(Playhead().ToFrameFloor(_rate), first, last);
        return (frame, Core.Playback.HoldAnimationRule.SourceTimeAt(clip.TimelineStart, clip.SourceIn, clip.Speed, frame, _rate));
    }

    private long FrameOfPoint(MediaBackedClip clip, MediaTime source)
    {
        long a = clip.Speed.Numerator, b = clip.Speed.Denominator;
        return (clip.TimelineStart + new MediaTime((long)((Int128)(source - clip.SourceIn).Ticks * b / a))).ToNearestFrame(_rate);
    }

    /// <summary>With points the field shows what plays on the playhead's frame: the clip's volume × the curve there.</summary>
    private void ShowVolumeAtPlayhead()
    {
        if (_clip is not MediaBackedClip media || AudioProperties.Of(_clip) is not { Volumes: { } points } audio || VolumeFrame(media) is not { } at)
            return;
        var wasSyncing = _syncing;
        _syncing = true;
        try { VolumePercent = Math.Min(200m, Math.Round((decimal)(audio.Volume * points.At(at.Source)) * 100m, 1)); }
        finally { _syncing = wasSyncing; }
    }

    /// <summary>
    /// The field set to <paramref name="target"/> (0–2) with points: on a keyframe's frame that point; between two the segment —
    /// both of its points scaled alike, so the whole segment reaches the value (the curve is even in dB); before the first / after
    /// the last that edge point. Without points (or a playhead) false: the clip's volume, as before.
    /// </summary>
    private bool EditVolumeAtPlayhead(double target)
    {
        if (_clip is not MediaBackedClip media || AudioProperties.Of(_clip) is not { Volumes: { } points } audio || VolumeFrame(media) is not { } at)
            return false;
        var onFrame = points.Points.FirstOrDefault(p => FrameOfPoint(media, p.Time) == at.Frame);
        var previous = points.Points.LastOrDefault(p => p.Time < at.Source);
        var next = points.Points.FirstOrDefault(p => p.Time > at.Source);
        var affected = onFrame is not null ? new[] { onFrame }
            : previous is not null && next is not null ? new[] { previous, next }
            : new[] { previous ?? next! };
        var current = audio.Volume * points.At(at.Source);
        var volume = Math.Max(audio.Volume, 1e-6);
        var updated = points;
        foreach (var point in affected)
        {
            var gain = current > 1e-6 ? point.Gain * target / current : target / volume;
            updated = updated.With(point with { Gain = Math.Round(Math.Clamp(gain, VolumeEnvelope.MinGain, VolumeEnvelope.MaxGain), 4) }) ?? updated;
        }
        EditAudio(a => a with { Volumes = updated });
        return true;
    }

    partial void OnSoundFadeInFramesChanged(decimal? value) => EditSoundFade(value, fadeIn: true);
    partial void OnSoundFadeOutFramesChanged(decimal? value) => EditSoundFade(value, fadeIn: false);

    private void EditSoundFade(decimal? value, bool fadeIn)
    {
        if (_syncing || value is not { } frames || _clip is not VideoClip) return;
        if (frames < 0 || frames != decimal.Truncate(frames))
        {
            _status.ReportWarning(Strings.Inspector_FadeWholeFrames);
            ShowModelAfterRejection();
            return;
        }
        var length = MediaTime.FromFrame((long)frames, _rate);
        EditAudio(a => fadeIn ? a with { SoundFadeIn = length } : a with { SoundFadeOut = length });
    }

    partial void OnNoiseReductionChanged(decimal? value) => EditCleanup(value, noise: true);
    partial void OnVoiceEnhancementChanged(decimal? value) => EditCleanup(value, noise: false);

    private void EditCleanup(decimal? value, bool noise)
    {
        if (_syncing || value is not { } strength || _clip is null) return;
        var whole = (int)Math.Clamp(Math.Round(strength, MidpointRounding.AwayFromZero), 0, AudioCleanup.MaxStrength);
        EditAudio(a =>
        {
            var current = a.Cleanup ?? AudioCleanup.None;
            var next = noise ? current with { NoiseReduction = whole } : current with { VoiceEnhancement = whole };
            return a with { Cleanup = AudioCleanup.Normalize(next) };
        });
    }

    [RelayCommand]
    private void ClearVolumePoints() => EditAudio(a =>
        // Amendment 1: a video clip's points are its keyframes — their gains are reset, the keyframes (its picture) stay.
        _clip is VideoClip { Keyframes: not null } && a.Volumes is { } points
            ? a with { Volumes = new VolumeEnvelope(points.Points.Select(p => p with { Gain = 1 }).ToImmutableArray()) }
            : a with { Volumes = null });

    [RelayCommand]
    private void CancelCleanup()
    {
        if (_clip is MediaBackedClip media && AudioProperties.Of(_clip) is { } audio) _audioProcessing?.Cancel(media.MediaAssetId, audio.Cleanup);
    }

    [RelayCommand]
    private void RetryCleanup() => _audioProcessing?.Retry();

    private void RefreshCleanupStatus()
    {
        if (_clip is not MediaBackedClip media || AudioProperties.Of(_clip) is not { } audio || AudioCleanup.Normalize(audio.Cleanup) is not { } cleanup)
        {
            (CleanupStatus, HasCleanupStatus, CanCancelCleanup, CanRetryCleanup) = (string.Empty, false, false, false);
            return;
        }
        var status = _audioProcessing?.CleanupStatus(media.MediaAssetId, cleanup) ?? new AudioJobStatus(AudioJobState.None);
        CleanupStatus = status.State switch
        {
            AudioJobState.Processing => string.Format(Strings.Inspector_CleanupProcessing, status.Progress * 100),
            AudioJobState.Waiting => Strings.Inspector_CleanupWaiting,
            AudioJobState.Ready => Strings.Inspector_CleanupReady,
            AudioJobState.Cancelled => Strings.Inspector_CleanupCancelled,
            AudioJobState.Failed => string.Format(Strings.Inspector_CleanupFailed, status.Message),
            _ => Strings.Inspector_CleanupWaiting
        };
        HasCleanupStatus = true;
        CanCancelCleanup = status.IsBusy;
        CanRetryCleanup = status.State is AudioJobState.Failed or AudioJobState.Cancelled;
    }
}
