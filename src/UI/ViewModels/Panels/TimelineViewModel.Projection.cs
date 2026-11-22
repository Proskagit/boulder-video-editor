using System.Collections.ObjectModel;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Rendering;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>The model projected into track and clip view models (split out of TimelineViewModel.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineViewModel
{
    // --- Projection ---------------------------------------------------------------

    /// <summary>Another project became current (New / Open / Recover). Selection and any drag
    /// belong to the old one; zoom and snapping are session state of the new sequence (D015),
    /// so they are read from it rather than carried over.</summary>
    private void OnProjectReplaced()
    {
        CancelGesture();
        _marquee = null;
        IsMarqueeVisible = false;
        _selection.Clear();
        // The new project's clips are new objects, and the same project opened again has the same ids: view models of
        // the previous project must not be reused for them.
        _clipViewModels.Clear();
        _transitionViewModels.Clear();
        _selectedTransitionId = null;
        Clipboard = null;   // copied clips refer to the previous project's media and tracks (D027 §5)
        PixelsPerSecond = Sequence.ZoomPixelsPerSecond;
        Refresh();
        _hadClips = HasClips();     // D068 item 6: an opened project keeps its saved zoom
        _userZoomed = false;
        PrepareInitialZoom();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(MagneticMainTrack));   // an Undo / Redo or another project
        // Video top-down as the playback snapshot composites it (PlaybackSnapshotBuilder: equal orders by their place in
        // the list, the later one on top), audio by order.
        var desiredTracks = Sequence.VideoTracks.Select((t, index) => (t, index))
            .OrderByDescending(x => x.t.Order).ThenByDescending(x => x.index).Select(x => x.t)
            .Concat(Sequence.AudioTracks.OrderBy(t => t.Order))
            .ToList();

        if (!desiredTracks.SequenceEqual(Tracks.Select(t => t.Track)))
        {
            Tracks.Clear();
            foreach (var track in desiredTracks)
            {
                if (!_trackViewModels.TryGetValue(track, out var vm))
                    _trackViewModels[track] = vm = new TimelineTrackViewModel(track);
                Tracks.Add(vm);
            }
            foreach (var stale in _trackViewModels.Keys.Except(desiredTracks).ToList())
                _trackViewModels.Remove(stale);
        }
        for (var i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].HasTrackAbove = i > 0 && Tracks[i - 1].Type == Tracks[i].Type;
            Tracks[i].HasTrackBelow = i < Tracks.Count - 1 && Tracks[i + 1].Type == Tracks[i].Type;
            Tracks[i].SyncState();
        }
        // D064: the mixer follows the model (the master, every track's volume, solo and ducking).
        Mixer.Sync(Tracks.Select(t => t.Track));
        foreach (var track in Tracks) track.Mix = Mixer.MixOf(track.Track);
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();

        var liveIds = new HashSet<Guid>();
        foreach (var trackVm in Tracks)
        {
            var desired = trackVm.Track.Clips.Select(GetClipViewModel).ToList();
            liveIds.UnionWith(desired.Select(c => c.Id));
            if (!desired.SequenceEqual(trackVm.Clips))
            {
                trackVm.Clips.Clear();
                foreach (var clipVm in desired) trackVm.Clips.Add(clipVm);
            }
        }

        foreach (var staleId in _clipViewModels.Keys.Where(id => !liveIds.Contains(id)).ToList())
            _clipViewModels.Remove(staleId);

        var liveTransitions = new HashSet<Guid>();
        foreach (var trackVm in Tracks)
        {
            var desired = trackVm.Track.Transitions.Select(t => GetTransitionViewModel(t, trackVm.Track)).ToList();
            liveTransitions.UnionWith(desired.Select(t => t.Id));
            if (!desired.SequenceEqual(trackVm.Transitions))
            {
                trackVm.Transitions.Clear();
                foreach (var transitionVm in desired) trackVm.Transitions.Add(transitionVm);
            }
        }
        foreach (var staleId in _transitionViewModels.Keys.Where(id => !liveTransitions.Contains(id)).ToList())
            _transitionViewModels.Remove(staleId);
        if (_selectedTransitionId is { } selected && !liveTransitions.Contains(selected))
            _selectedTransitionId = null;
        IsEmpty = liveIds.Count == 0;
        RefreshClipNames(); // a text clip's label is its text, which any change (or undo) may alter
        _selection.RemoveAll(id => !liveIds.Contains(id));

        FrameRateDisplay = _projectService.Current.Settings.IsFrameRateLocked
            ? string.Format(Strings.Rate_Fps, FormatRate(FrameRate))
            : string.Format(Strings.Rate_FpsProvisional, FormatRate(FrameRate));

        TracksHeight = Tracks.Count * TimelineTrackViewModel.Height;
        PixelsPerSecond = TimelineCoordinateMapper.ClampZoom(PixelsPerSecond, FrameRate);

        // The frame rate may have just changed (first video): keep the playhead on the grid — only then (D057 §3.14: the
        // playhead may lie between the project's frames on a source frame, and an edit must not move it).
        if (_gridRate != FrameRate)
        {
            _gridRate = FrameRate;
            Sequence.PlayheadPosition = Playhead.SnapToFrame(FrameRate);
        }

        Relayout();
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
        RaiseTransitionSelectionChanged();
        PlayheadChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimelineTransitionViewModel GetTransitionViewModel(Transition transition, Track track)
    {
        if (!_transitionViewModels.TryGetValue(transition.Id, out var vm) || vm.Track != track)
            _transitionViewModels[transition.Id] = vm = new TimelineTransitionViewModel(transition, track);
        return vm;
    }

    private TimelineClipViewModel GetClipViewModel(Clip clip)
    {
        // Reused only for the same clip object: a view model shows and hands out the clip it wraps.
        if (!_clipViewModels.TryGetValue(clip.Id, out var vm) || vm.Clip != clip)
            _clipViewModels[clip.Id] = vm = new TimelineClipViewModel(clip, ClipName(clip));
        vm.IsLinked = clip is MediaBackedClip { LinkId: not null };   // D032 R12: the mark follows Link / Unlink / Undo
        vm.HasCleanup = clip switch                                  // D066: the clean-up badge follows its edits and Undo
        {
            VideoClip v => v.Cleanup is { } c && c != Core.Audio.AudioCleanup.None,
            AudioClip a => a.Cleanup is { } c && c != Core.Audio.AudioCleanup.None,
            _ => false,
        };
        return vm;
    }

    private void RefreshClipNames()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.Name = ClipName(vm.Clip);
    }

    private string ClipName(Clip clip) => clip switch
    {
        MediaBackedClip m => FindAsset(m.MediaAssetId)?.FileName ?? Strings.Clip_MissingMedia,
        TextClip t => TextLabel(t.Text),
        TemplateClip p => TemplateLabel(p),
        _ => Strings.Clip_Generic
    };

    /// <summary>A template's name on the timeline: its kind, with what it shows when that says more — «Боулдер · 7 · 7A»,
    /// «Хайлайт · ЛУЧШАЯ ПОПЫТКА».</summary>
    public static string TemplateLabel(TemplateClip clip)
    {
        var name = TemplateTypeName(clip.Kind);
        var detail = clip.Kind switch
        {
            TemplateKind.Boulder => string.Join(" · ", new[] { clip.Route, clip.Grade }.Where(s => s.Length > 0)),
            TemplateKind.Highlight => clip.Title,
            TemplateKind.HeartRate => string.Empty,
            TemplateKind.Summary => clip.Title,
            _ => clip.Date
        };
        return detail.Length > 0 ? string.Format(Strings.Clip_TemplateWith, name, detail) : name;
    }

    public static string TemplateTypeName(TemplateKind kind) => kind switch
    {
        TemplateKind.Rec => Strings.TemplateType_Rec,
        TemplateKind.Vhs => Strings.TemplateType_Vhs,
        TemplateKind.Boulder => Strings.TemplateType_Boulder,
        TemplateKind.HeartRate => Strings.TemplateType_HeartRate,
        TemplateKind.Summary => Strings.TemplateType_Summary,
        _ => Strings.TemplateType_Highlight
    };

    public static string EmptyTextLabel => Strings.Clip_EmptyText;

    /// <summary>First line of a text clip's text (any line break), or <see cref="EmptyTextLabel"/>
    /// when the text is empty or only whitespace (such a clip draws nothing).</summary>
    public static string TextLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmptyTextLabel;
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        return end < 0 ? text : text[..end];
    }

    private MediaAsset? FindAsset(Guid id) => _projectService.Current.MediaAssets.FirstOrDefault(a => a.Id == id);

    /// <summary>Whether a dissolve sits on the clip's start / end (D025 PO-8: the clip's fade there is not applied).</summary>
    private (bool AtStart, bool AtEnd) Dissolves(Clip clip)
    {
        var track = Sequence.VideoTracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null) return (false, false);
        return (track.Transitions.Any(t => t.RightClipId == clip.Id), track.Transitions.Any(t => t.LeftClipId == clip.Id));
    }

    /// <summary>Gives every clip the pixel width of its effective fade ramps (D025 §2, PO-8) at the current zoom.</summary>
    private void RefreshFades()
    {
        foreach (var vm in _clipViewModels.Values)
        {
            var clip = vm.Clip;
            if (clip.FadeIn == MediaTime.Zero && clip.FadeOut == MediaTime.Zero)
            {
                (vm.FadeInWidth, vm.FadeOutWidth) = (0, 0);
                continue;
            }
            var (atStart, atEnd) = Dissolves(clip);
            var (fadeIn, fadeOut) = FadeRule.EffectiveFrames(clip, FrameRate, atStart, atEnd);
            var startFrame = clip.TimelineStart.ToFrameFloor(FrameRate);
            var endFrame = clip.TimelineEnd.ToFrameFloor(FrameRate);
            vm.FadeInWidth = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(startFrame + fadeIn, FrameRate) - clip.TimelineStart, PixelsPerSecond);
            vm.FadeOutWidth = TimelineCoordinateMapper.TimeToX(clip.TimelineEnd - MediaTime.FromFrame(endFrame - fadeOut, FrameRate), PixelsPerSecond);
        }
    }

    /// <summary>Places the status mark of every «Боулдер» with a status (D045 §17) at the current zoom: the frame of its
    /// status moment on the template's clock (<see cref="TemplateClip.CounterStart"/> at the clip's start), when inside it.</summary>
    private void RefreshStatusMarks()
    {
        foreach (var vm in _clipViewModels.Values)
        {
            if (vm.Clip is not TemplateClip { Kind: TemplateKind.Boulder } template || template.Status == BoulderStatus.None)
            {
                vm.StatusMarkX = null;
                continue;
            }
            var offset = template.StatusAt.ToNearestFrame(FrameRate) - template.CounterStart.ToNearestFrame(FrameRate);
            var frames = template.TimelineEnd.ToFrameFloor(FrameRate) - template.TimelineStart.ToFrameFloor(FrameRate);
            vm.StatusMarkX = offset >= 0 && offset < frames
                ? TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(offset, FrameRate), PixelsPerSecond)
                : null;
            vm.OnStatusChanged();
        }
        RefreshStrips();
        RefreshRouteStartMarks();
        RefreshFollowStrips();
        RefreshKeyframeMarkers();
    }

    /// <summary>Recomputes every pixel position from the model at the current zoom.</summary>
    private void Relayout()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.Layout(PixelsPerSecond);
        RefreshWaveforms();
        RefreshFilmstrips();
        RefreshFades();
        RefreshVolumeShapes();
        RefreshStatusMarks();
        foreach (var vm in _transitionViewModels.Values)
            vm.Layout(PixelsPerSecond, FrameRate);

        var contentEnd = TimelineCoordinateMapper.TimeToX(SequenceDuration + TrailingSpace, PixelsPerSecond);
        ContentWidth = Math.Max(contentEnd, _viewportWidth);
        PlayheadX = TimelineCoordinateMapper.TimeToX(Playhead, PixelsPerSecond);
        RebuildRuler();
        RebuildMarkers();
        RefreshRange();
    }
}
