using System.Collections.ObjectModel;
using System.Globalization;
using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A track another track can duck under (the ducking's speech track choice).</summary>
public sealed record SpeechTrackChoice(Guid Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// D064 §1 / §2 / §4: one track's mix as its header's flyout and the mixer show it — volume (per cent, the dB beside it), mute,
/// solo, and the ducking (on / off, the speech track, depth, attack, release, sensitivity, its state, «Закрепить в точки»).
/// The model is the truth: <see cref="Sync"/> copies it after every timeline change; a change goes through the edit service as
/// one undo step (a slider's drag merges into one).
/// </summary>
public sealed partial class TrackMixViewModel : ViewModelBase
{
    private readonly MixerViewModel _mixer;
    private bool _syncing;

    public TrackMixViewModel(MixerViewModel mixer, Track track)
    {
        _mixer = mixer;
        Track = track;
        Sync();
    }

    public Track Track { get; }

    public string Name => Track.Name;

    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isSolo;

    /// <summary>The track's volume in per cent (0–200).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    private double _volumePercent = 100;

    public string VolumeText => MixerViewModel.Db(VolumePercent / 100);

    [ObservableProperty] private bool _isDucking;
    [ObservableProperty] private IReadOnlyList<SpeechTrackChoice> _speechChoices = Array.Empty<SpeechTrackChoice>();
    [ObservableProperty] private SpeechTrackChoice? _speechTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DepthText))]
    private double _depthDb = DuckingSettings.DefaultDepthDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttackText))]
    private double _attackMs = DuckingSettings.DefaultAttackMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseText))]
    private double _releaseMs = DuckingSettings.DefaultReleaseMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SensitivityText))]
    private double _sensitivity = DuckingSettings.DefaultSensitivity;

    public string DepthText => string.Format(CultureInfo.InvariantCulture, "{0:0} дБ", DepthDb).Replace('-', '−');
    public string AttackText => string.Format(Strings.Mixer_Milliseconds, AttackMs);
    public string ReleaseText => string.Format(Strings.Mixer_Milliseconds, ReleaseMs);
    public string SensitivityText => string.Format(CultureInfo.InvariantCulture, "{0:0}", Sensitivity);

    /// <summary>The ducking's state (the speech still detected, failed, ready) for the flyout; empty when off.</summary>
    [ObservableProperty] private string _duckingStatus = string.Empty;
    [ObservableProperty] private bool _canRetryDucking;
    [ObservableProperty] private bool _canCancelDucking;
    [ObservableProperty] private bool _canBakeDucking;

    public void Sync()
    {
        _syncing = true;
        try
        {
            IsMuted = Track.IsMuted;
            IsSolo = Track.IsSolo;
            VolumePercent = Math.Round(Track.Volume * 100, 1);
            SpeechChoices = _mixer.SpeechChoicesFor(Track);
            IsDucking = Track.Ducking is not null;
            if (Track.Ducking is { } d)
            {
                SpeechTrack = SpeechChoices.FirstOrDefault(c => c.Id == d.SpeechTrackId);
                (DepthDb, AttackMs, ReleaseMs, Sensitivity) = (d.DepthDb, d.AttackMs, d.ReleaseMs, d.Sensitivity);
            }
            SyncStatus();
        }
        finally
        {
            _syncing = false;
        }
    }

    public void SyncStatus()
    {
        var status = Track.Ducking is null ? new AudioJobStatus(AudioJobState.None) : _mixer.DuckingStatus(Track.Id);
        DuckingStatus = Track.Ducking is null ? string.Empty : status.State switch
        {
            AudioJobState.Processing => string.Format(Strings.Mixer_DuckingDetecting, status.Progress * 100),
            AudioJobState.Waiting => Strings.Mixer_DuckingWaiting,
            AudioJobState.Cancelled => Strings.Mixer_DuckingCancelled,
            AudioJobState.Failed => status.Message ?? Strings.Mixer_DuckingFailed,
            AudioJobState.Ready => Strings.Mixer_DuckingReady,
            _ => Strings.Mixer_DuckingNoSpeech
        };
        CanRetryDucking = status.State is AudioJobState.Failed or AudioJobState.Cancelled;
        CanCancelDucking = status.IsBusy;
        CanBakeDucking = status.State == AudioJobState.Ready && !Track.IsLocked;
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing) _mixer.SetMuted(this, value);
    }

    partial void OnIsSoloChanged(bool value)
    {
        if (!_syncing) _mixer.SetSolo(this, value);
    }

    partial void OnVolumePercentChanged(double value)
    {
        if (!_syncing) _mixer.SetVolume(this, value / 100);
    }

    partial void OnIsDuckingChanged(bool value)
    {
        if (_syncing) return;
        if (!value) { _mixer.SetDucking(this, null); return; }
        var speech = SpeechTrack ?? SpeechChoices.FirstOrDefault();
        if (speech is null)
        {
            _mixer.Report(Strings.Mixer_NoSpeechTrack);
            Sync();
            return;
        }
        _mixer.SetDucking(this, DuckingSettings.Default(speech.Id));
    }

    partial void OnSpeechTrackChanged(SpeechTrackChoice? value) => PushDucking();
    partial void OnDepthDbChanged(double value) => PushDucking(slider: true);
    partial void OnAttackMsChanged(double value) => PushDucking(slider: true);
    partial void OnReleaseMsChanged(double value) => PushDucking(slider: true);
    partial void OnSensitivityChanged(double value) => PushDucking(slider: true);

    private void PushDucking(bool slider = false)
    {
        if (_syncing || !IsDucking || SpeechTrack is not { } speech) return;
        _mixer.SetDucking(this, new DuckingSettings(speech.Id, Math.Round(DepthDb, 1), (int)Math.Round(AttackMs), (int)Math.Round(ReleaseMs),
            (int)Math.Round(Sensitivity)), slider);
    }

    [RelayCommand] private void RetryDucking() => _mixer.RetryAnalyses();
    [RelayCommand] private void CancelDucking() => _mixer.CancelDucking(this);
    [RelayCommand] private void BakeDucking() => _mixer.BakeDucking(this);
}

/// <summary>
/// D064 §1 / §3: the mixer — the master volume and every track's mix (<see cref="TrackMixViewModel"/>), in the tool bar's
/// «Микшер» flyout and the tracks' headers. Owned by the timeline; changes go through the edit service.
/// </summary>
public sealed partial class MixerViewModel : ViewModelBase
{
    private readonly IProjectService _projects;
    private readonly ITimelineEditService _edit;
    private readonly StatusService _status;
    private readonly AudioProcessingCoordinator? _audio;
    private readonly Func<bool> _canEdit;
    private bool _syncing;
    private object? _gesture;
    private DateTime _gestureAt;

    public MixerViewModel(IProjectService projects, ITimelineEditService edit, StatusService status, AudioProcessingCoordinator? audio, Func<bool> canEdit)
    {
        _projects = projects;
        _edit = edit;
        _status = status;
        _audio = audio;
        _canEdit = canEdit;
        if (_audio is not null) _audio.StatusChanged += (_, _) => { foreach (var t in Tracks) t.SyncStatus(); };
    }

    public ObservableCollection<TrackMixViewModel> Tracks { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterText))]
    private double _masterPercent = 100;

    public string MasterText => Db(MasterPercent / 100);

    /// <summary>D064 §5: the Preview plays every clip's original sound (the comparison; never saved, never exported).</summary>
    public bool OriginalSound
    {
        get => _audio?.OriginalSound ?? false;
        set
        {
            if (_audio is null || _audio.OriginalSound == value) return;
            _audio.OriginalSound = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The mix of <paramref name="track"/> (one per track, kept across syncs).</summary>
    public TrackMixViewModel MixOf(Track track)
    {
        var mix = Tracks.FirstOrDefault(t => t.Track == track);
        if (mix is not null) return mix;
        mix = new TrackMixViewModel(this, track);
        Tracks.Add(mix);
        return mix;
    }

    /// <summary>After every timeline change: the master, the tracks (in the timeline's order), their states.</summary>
    public void Sync(IEnumerable<Track> order)
    {
        _syncing = true;
        try { MasterPercent = Math.Round(_projects.Current.Timeline.MasterVolume * 100, 1); }
        finally { _syncing = false; }
        var tracks = order.ToList();
        foreach (var gone in Tracks.Where(t => !tracks.Contains(t.Track)).ToList()) Tracks.Remove(gone);
        for (var i = 0; i < tracks.Count; i++)
        {
            var mix = MixOf(tracks[i]);
            var at = Tracks.IndexOf(mix);
            if (at != i) Tracks.Move(at, i);
            mix.Sync();
        }
        OnPropertyChanged(nameof(OriginalSound));
    }

    partial void OnMasterPercentChanged(double value)
    {
        if (_syncing) return;
        Report(_edit.SetMasterVolume(Math.Clamp(value / 100, 0, 2), Gesture()));
    }

    public static string Db(double gain) => gain <= 0
        ? "−∞ дБ"
        : string.Format(CultureInfo.InvariantCulture, "{0:+0.0;−0.0;0.0} дБ", 20 * Math.Log10(gain));

    internal IReadOnlyList<SpeechTrackChoice> SpeechChoicesFor(Track track) =>
        _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks)
            .Where(t => t != track).Select(t => new SpeechTrackChoice(t.Id, t.Name)).ToList();

    internal AudioJobStatus DuckingStatus(Guid trackId) => _audio?.DuckingStatus(trackId) ?? new AudioJobStatus(AudioJobState.Failed, 0,
        Core.Localization.Messages.Audio_SpeechModelUnavailable);

    /// <summary>The changes of one slider drag share a gesture: a change within 0.8 s of the last.</summary>
    private object Gesture()
    {
        var now = DateTime.UtcNow;
        if (_gesture is null || now - _gestureAt > TimeSpan.FromMilliseconds(800)) _gesture = new object();
        _gestureAt = now;
        return _gesture;
    }

    internal void SetMuted(TrackMixViewModel mix, bool muted) =>
        Report(_edit.SetTrackMuted(mix.Track.Id, muted), string.Format(muted ? Strings.Track_Muted : Strings.Track_Unmuted, mix.Track.Name), mix);

    internal void SetSolo(TrackMixViewModel mix, bool solo) =>
        Report(_edit.SetTrackSolo(mix.Track.Id, solo), string.Format(solo ? Strings.Track_Soloed : Strings.Track_Unsoloed, mix.Track.Name), mix);

    internal void SetVolume(TrackMixViewModel mix, double volume) =>
        Report(_edit.SetTrackVolume(mix.Track.Id, Math.Clamp(volume, 0, 2), Gesture()), null, mix);

    internal void SetDucking(TrackMixViewModel mix, DuckingSettings? ducking, bool slider = false) =>
        Report(_edit.SetTrackDucking(mix.Track.Id, ducking, slider ? Gesture() : null),
            slider ? null : string.Format(ducking is null ? Strings.Mixer_DuckingOff : Strings.Mixer_DuckingOn, mix.Track.Name), mix);

    internal void RetryAnalyses() => _audio?.Retry();

    internal void CancelDucking(TrackMixViewModel mix) => _audio?.CancelDucking(mix.Track.Id);

    /// <summary>«Закрепить в точки»: the ducking as it sounds now written into the track's clips' points; the ducking off.</summary>
    internal void BakeDucking(TrackMixViewModel mix)
    {
        if (mix.Track.Ducking is not { } ducking || _audio is null) return;
        var speech = _projects.Current.Timeline.VideoTracks.Concat(_projects.Current.Timeline.AudioTracks).FirstOrDefault(t => t.Id == ducking.SpeechTrackId);
        if (speech is null) return;
        var curve = PlaybackSnapshotBuilder.DuckingCurveOf(speech, ducking, _audio.Analyses.Speech);
        Report(_edit.BakeDucking(mix.Track.Id, curve), string.Format(Strings.Mixer_DuckingBaked, mix.Track.Name), mix);
    }

    internal void Report(string message) => _status.Report(message);

    private void Report(TimelineEditResult result, string? success = null, TrackMixViewModel? resync = null)
    {
        if (!result.Success)
        {
            _status.Report(result.Message ?? Strings.Timeline_EditNotPossible);
            resync?.Sync();                       // the control shows the model again
        }
        else if (success is not null && !result.NoChange) _status.Report(success);
    }

    public bool CanEdit => _canEdit();
}
