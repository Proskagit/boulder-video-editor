using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.UI.Common;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// One clip's rectangle on the timeline. Wraps the real <see cref="Core.Entities.Clip"/>
/// (reused across refreshes by Id, so selection and pointer capture survive model
/// changes). <see cref="Left"/>/<see cref="Width"/> come from the clip's timing and
/// the zoom level — or from a drag/trim preview, which never touches the model.
/// </summary>
public sealed partial class TimelineClipViewModel : ViewModelBase
{
    public const double MinWidthPixels = 2;

    public TimelineClipViewModel(Clip clip, string name)
    {
        Clip = clip;
        _name = name;
    }

    public Clip Clip { get; }
    public Guid Id => Clip.Id;

    /// <summary>The label on the rectangle; follows the model (file name, text of a text clip).</summary>
    [ObservableProperty] private string _name;

    public string ColorHex => Clip switch
    {
        VideoClip => "#3A5A78",
        AudioClip => "#3A784F",
        ImageClip => "#78703A",
        _ => "#5A3A78"
    };

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _width;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True while a drag preview shows a position the edit would reject.</summary>
    [ObservableProperty] private bool _isInvalid;

    /// <summary>While the clip is slipped (D030 §7, Q12): its planned Source In / Out, shown on the clip; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSlipping))]
    private string? _slipText;

    public bool IsSlipping => SlipText is not null;

    /// <summary>What the clip's waveform shows (D024 Step 9.5), or null — a text or image clip, media without sound,
    /// offline media without a cached waveform, or not made yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWaveform))]
    private ClipWaveform? _waveform;

    public bool HasWaveform => Waveform is not null;

    /// <summary>Width in pixels of the fade in / fade out ramp drawn on the clip (D025 §2): the effective fade — clamped to
    /// the clip, none on an edge with a dissolve (PO-8) —, 0 without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFadeIn))]
    private double _fadeInWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFadeOut))]
    private double _fadeOutWidth;

    public bool HasFadeIn => FadeInWidth > 0;
    public bool HasFadeOut => FadeOutWidth > 0;

    public void Layout(double pixelsPerSecond) => Layout(pixelsPerSecond, Clip.TimelineStart, Clip.TimelineEnd);

    public void Layout(double pixelsPerSecond, MediaTime start, MediaTime end)
    {
        Left = TimelineCoordinateMapper.TimeToX(start, pixelsPerSecond);
        Width = Math.Max(MinWidthPixels, TimelineCoordinateMapper.TimeToX(end - start, pixelsPerSecond));
    }
}

/// <summary>One horizontal track lane.</summary>
public sealed partial class TimelineTrackViewModel : ViewModelBase
{
    public const double Height = 44;

    public TimelineTrackViewModel(Track track)
    {
        Track = track;
        SyncState();
    }

    public Track Track { get; }
    public string Label => Track.Name;
    public TrackType Type => Track.Type;

    /// <summary>Only a video track has a picture to hide (D030 §4): an audio track's header shows no hide control.</summary>
    public bool CanHide => Track.Type == TrackType.Video;

    /// <summary>The track's state flags as the header shows them (D030 §4); the model is the truth —
    /// <see cref="SyncState"/> copies it after every timeline change, Undo and Redo included.</summary>
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isHidden;
    [ObservableProperty] private bool _isLocked;

    public void SyncState()
    {
        IsMuted = Track.IsMuted;
        IsHidden = Track.IsHidden;
        IsLocked = Track.IsLocked;
    }

    public ObservableCollection<TimelineClipViewModel> Clips { get; } = new();

    /// <summary>The track's dissolves, drawn over the clips at their zones (D025 §3).</summary>
    public ObservableCollection<TimelineTransitionViewModel> Transitions { get; } = new();

    /// <summary>Highlighted while a drag/drop would land on this track.</summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>A track of the same kind is drawn above / below this one in the timeline (D027 §3) — where the header's
    /// arrows can move it. Whether the move is allowed (locks) is the edit service's call.</summary>
    [ObservableProperty] private bool _hasTrackAbove;
    [ObservableProperty] private bool _hasTrackBelow;
}

/// <summary>One dissolve's zone on the timeline (D025 §3): <c>[c − ⌊F/2⌋, c + ⌈F/2⌉)</c> in pixels at the current zoom.
/// Selecting it shows it in the Inspector; Delete removes it.</summary>
public sealed partial class TimelineTransitionViewModel(Transition transition, Track track) : ViewModelBase
{
    public Transition Transition { get; } = transition;
    public Track Track { get; } = track;
    public Guid Id => Transition.Id;

    [ObservableProperty] private double _left;
    [ObservableProperty] private double _width;
    [ObservableProperty] private bool _isSelected;

    /// <summary>False while a drag or trim preview would open the dissolve's cut (it is removed on release) —
    /// shown again when the gesture ends.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>True when <paramref name="contentX"/> lies in the zone as drawn.</summary>
    public bool Contains(double contentX) => IsVisible && contentX >= Left && contentX < Left + Width;

    /// <summary>Lays the zone out around the cut, moved by <paramref name="frameDelta"/> frames (a drag preview of both
    /// clips).</summary>
    public void Layout(double pixelsPerSecond, FrameRate rate, long frameDelta = 0)
    {
        var right = Track.Clips.FirstOrDefault(c => c.Id == Transition.RightClipId);
        if (right is null) return;
        var (beforeCut, afterCut) = TransitionRules.Zone(TransitionRules.Frames(Transition.Duration, rate));
        var cut = right.TimelineStart.ToFrameFloor(rate) + frameDelta;
        var start = MediaTime.FromFrame(cut - beforeCut, rate);
        Left = TimelineCoordinateMapper.TimeToX(start, pixelsPerSecond);
        Width = Math.Max(TimelineClipViewModel.MinWidthPixels,
            TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(cut + afterCut, rate) - start, pixelsPerSecond));
    }
}

/// <summary>The selected dissolve, as passed to the Inspector: its track, the names of A and B, the rate and the
/// longest it could be now (from the edit service; null when its cut is gone).</summary>
public sealed record TimelineTransitionSelection(Transition Transition, Track Track, string LeftName, string RightName, FrameRate Rate,
    long? MaxFrames);

/// <summary>A single labeled tick on the time ruler.</summary>
public sealed class TimelineRulerTickViewModel : ViewModelBase
{
    public required string Label { get; init; }
    public required double Left { get; init; }
}

/// <summary>A marker drawn on the ruler (D027 §6) at its position in pixels at the current zoom.</summary>
public sealed class TimelineMarkerViewModel : ViewModelBase
{
    public required Guid Id { get; init; }
    public required double Left { get; init; }
    public required string Color { get; init; }
}

/// <summary>The primary selected timeline clip, as passed to the Inspector. <paramref name="DissolveAtStart"/> /
/// <paramref name="DissolveAtEnd"/>: a dissolve sits on that edge, so the clip's fade there is not applied (D025 PO-8).</summary>
public sealed record TimelineClipSelection(Clip Clip, string Name, MediaAsset? Asset, FrameRate Rate,
    bool DissolveAtStart = false, bool DissolveAtEnd = false);
