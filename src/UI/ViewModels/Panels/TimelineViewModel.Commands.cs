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

/// <summary>The editing commands (adding media, text and templates, drops and their ghost), trim to the playhead, ripple delete, copy / paste, markers, tracks and their state (split out, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineViewModel
{
    // --- Editing commands ---------------------------------------------------------
    // Disabled while an export runs (EditingLock): they would change the timeline. Viewing (playhead, zoom,
    // selection, snapping) stays available; pointer edits and drops are ignored.

    /// <summary>False while editing is locked (an export runs).</summary>
    public bool IsEditingAllowed => !_editingLock.IsLocked;

    private bool CanEdit() => !_editingLock.IsLocked;

    private bool CanDeleteSelected() => CanEdit() && (HasSelection || HasTransitionSelection || SelectedOrangeClipId is not null || SelectedKeyframe is not null || _selectedVolumePoint is not null);

    /// <summary>The Dissolve command needs two selected clips on one track (a linked clip brings its partner into the
    /// selection, AQ1: the clips of the primary's track count); whether they meet and how long a dissolve — a crossfade on
    /// an audio track (D032 R9) — fits there is the edit service's call (D025 §3–§5).</summary>
    private bool CanAddDissolve() => CanEdit() && DissolvePair() is not null;

    /// <summary>D069 amendment 2: the selected clip's cut nearest the playhead — its start with the previous clip or its end with
    /// the next one (the one there is when only one is).</summary>
    private (Clip Left, Clip Right)? Neighbour(Track track, Clip clip)
    {
        var rate = FrameRate;
        long start = clip.TimelineStart.ToNearestFrame(rate), end = clip.TimelineEnd.ToNearestFrame(rate);
        var next = track.Clips.FirstOrDefault(c => c.TimelineStart.ToNearestFrame(rate) == end);
        var previous = track.Clips.FirstOrDefault(c => c.TimelineEnd.ToNearestFrame(rate) == start);
        if (next is null) return previous is null ? null : (previous, clip);
        if (previous is null) return (clip, next);
        var at = Playhead.ToNearestFrame(rate);
        return Math.Abs(at - start) < Math.Abs(at - end) ? (previous, clip) : (clip, next);
    }

    private (Clip Left, Clip Right)? CutAtPlayhead()
    {
        var rate = FrameRate;
        if (Sequence.VideoTracks.OrderBy(t => t.Order).FirstOrDefault() is not { } main) return null;
        var at = Playhead.ToNearestFrame(rate);
        var right = main.Clips.FirstOrDefault(c => c.TimelineStart.ToNearestFrame(rate) == at);
        var left = main.Clips.FirstOrDefault(c => c.TimelineEnd.ToNearestFrame(rate) == at);
        return left is not null && right is not null ? (left, right) : null;
    }

    private (Clip Left, Clip Right)? DissolvePair()
    {
        // D069 amendment 1: two clips of one track — their cut (as before); one (a linked partner on another track aside) — its
        // cut with the next clip, else the previous one; none — the cut under the playhead on the main track.
        if (_selection.Count == 0) return CutAtPlayhead();
        if (!_clipViewModels.TryGetValue(_selection[^1], out var primary)) return null;
        var track = TrackOf(primary);
        var clips = _selection.Select(id => _clipViewModels.TryGetValue(id, out var vm) ? vm : null)
            .Where(vm => vm is not null && TrackOf(vm) == track).Select(vm => vm!.Clip).OrderBy(c => c.TimelineStart).ToList();
        if (clips.Count == 2) return (clips[0], clips[1]);
        return clips.Count == 1 && track is not null ? Neighbour(track.Track, clips[0]) : null;
    }

    private void OnEditingLockChanged()
    {
        if (_editingLock.IsLocked)
        {
            CancelGesture();
            CancelFollowDrag();
            CancelKeyframeDrag();
            CancelVolumeDrag();   // D064 §2: the values before the drag come back
        }
        OnPropertyChanged(nameof(IsEditingAllowed));
        NotifyKeyframeCommands();
        NotifyLinkCommands();
        SplitAtPlayheadCommand.NotifyCanExecuteChanged();
        TrimStartToPlayheadCommand.NotifyCanExecuteChanged();
        TrimEndToPlayheadCommand.NotifyCanExecuteChanged();
        RippleTrimStartToPlayheadCommand.NotifyCanExecuteChanged();
        RippleTrimEndToPlayheadCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RippleDeleteCommand.NotifyCanExecuteChanged();
        CloseGapCommand.NotifyCanExecuteChanged();
        PasteCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        FillFrameCommand.NotifyCanExecuteChanged();
        FitFrameCommand.NotifyCanExecuteChanged();
        NudgeLeftCommand.NotifyCanExecuteChanged();
        NudgeRightCommand.NotifyCanExecuteChanged();
        NudgeLeftSecondCommand.NotifyCanExecuteChanged();
        NudgeRightSecondCommand.NotifyCanExecuteChanged();
        AddDissolveCommand.NotifyCanExecuteChanged();
        AddTransitionCommand.NotifyCanExecuteChanged();
        OpenTransitionMenuCommand.NotifyCanExecuteChanged();
        AddVideoTrackCommand.NotifyCanExecuteChanged();
        AddAudioTrackCommand.NotifyCanExecuteChanged();
        AddTextCommand.NotifyCanExecuteChanged();
        AddTemplateCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();
        ToggleTrackMuteCommand.NotifyCanExecuteChanged();
        ToggleTrackHiddenCommand.NotifyCanExecuteChanged();
        ToggleTrackLockCommand.NotifyCanExecuteChanged();
        AddMarkerCommand.NotifyCanExecuteChanged();
        RemoveMarkerCommand.NotifyCanExecuteChanged();
        NotifySourceCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void SplitAtPlayhead() =>
        Report(_edit.Split(EditPlayhead, _selection.Count > 0 ? _selection.ToList() : null));

    // --- Trim to the playhead (D030 §5): Q / W plain, Shift+Q / Shift+W ripple ----------------------------
    // The selected clips with the playhead inside them (Q1) — with none selected, the video under the playhead (D074 §1);
    // every rule is the edit service's. After a ripple trim
    // of the start the playhead goes to the clip's start (Q5) — session state, not part of the undo step.

    [RelayCommand(CanExecute = nameof(CanEdit))] private void TrimStartToPlayhead() => TrimToPlayhead(ClipEdge.Start, ripple: false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void TrimEndToPlayhead() => TrimToPlayhead(ClipEdge.End, ripple: false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void RippleTrimStartToPlayhead() => TrimToPlayhead(ClipEdge.Start, ripple: true);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void RippleTrimEndToPlayhead() => TrimToPlayhead(ClipEdge.End, ripple: true);

    private void TrimToPlayhead(ClipEdge edge, bool ripple)
    {
        var result = _edit.TrimToPlayhead(_selection.ToList(), edge, EditPlayhead, ripple);   // none selected: the video under the playhead (D074 §1)
        if (result is { Success: true, Playhead: { } playhead }) SetPlayhead(playhead);
        var count = result.ClipIds.Count;
        var (one, many) = (ripple, edge == ClipEdge.Start) switch
        {
            (false, true) => (Strings.Trim_StartOne, Strings.Trim_StartMany),
            (false, false) => (Strings.Trim_EndOne, Strings.Trim_EndMany),
            (true, true) => (Strings.Trim_RippleStartOne, Strings.Trim_RippleStartMany),
            (true, false) => (Strings.Trim_RippleEndOne, Strings.Trim_RippleEndMany)
        };
        Report(result, successMessage: count == 1 ? one : Plural.Format(count, many));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (DeleteSelectedKeyframe()) return;   // D063 §2: the selected keyframe, not its clip
        if (DeleteSelectedVolumePoint()) return;   // D064 §2: the selected volume point, not its clip
        if (SelectedOrangeClipId is { } markupClip)
        {
            DeleteSelectedOrange(markupClip);   // D048 §3.9: the strip's selection, not the clip's
            return;
        }
        if (_selectedTransitionId is { } transitionId && _selection.Count == 0)
        {
            var library = _transitionViewModels.TryGetValue(transitionId, out var zone) && zone.Track.Type == TrackType.Video &&
                zone.Transition.TransitionTypeId != TransitionRules.CrossDissolve;
            Report(_edit.RemoveTransition(transitionId), successMessage: library ? Strings.Transition_Removed : Strings.Dissolve_Removed);
            return;
        }

        var result = _edit.DeleteClips(_selection.ToList());
        if (result.Success) ClearSelection();
        Report(result);
    }

    /// <summary>Raised by <see cref="OpenTransitionMenuCommand"/> for two clips of a video track: the view shows the menu
    /// of the five types (D044 §6).</summary>
    public event EventHandler? TransitionMenuRequested;

    /// <summary>"Transition" in the header (D044 §6): on a video track the menu of the library's types; on an audio track
    /// the crossfade, as before (D032), without a menu.</summary>
    [RelayCommand(CanExecute = nameof(CanAddDissolve))]
    private void OpenTransitionMenu()
    {
        if (DissolvePair() is not var (left, _)) return;
        if (left is AudioClip) AddDissolve();
        else TransitionMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A transition of <paramref name="typeId"/> on the cut between the two selected clips (D044 §6): its default
    /// duration, or the longest that fits when that is shorter (the status says so); never another type. Every check is
    /// the edit service's and its message is shown when it refuses. The new transition is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddDissolve))]
    private void AddTransition(string? typeId)
    {
        if (DissolvePair() is not var (left, right) || typeId is null) return;
        var selectVideo = typeId == TransitionRules.CrossDissolve ? Strings.Dissolve_SelectVideo : Strings.Transition_SelectVideo;
        if (left is AudioClip && typeId != TransitionRules.Crossfade)
        {
            _status.ReportWarning(selectVideo);
            return;
        }
        if (_edit.MaxTransitionFrames(left.Id, right.Id, typeId) is null)   // the clips don't meet on one track
        {
            _status.ReportWarning(left is AudioClip ? Strings.Dissolve_SelectAudio : selectVideo);
            return;
        }

        var result = _edit.AddTransition(left.Id, right.Id, typeId);
        if (!result.Success)
        {
            Report(result);
            return;
        }
        if (result.TransitionId is { } id) SelectTransition(id);
        var frames = Sequence.VideoTracks.Concat(Sequence.AudioTracks).SelectMany(t => t.Transitions)
            .Where(t => t.Id == result.TransitionId).Select(t => TransitionRules.Frames(t.Duration, FrameRate)).FirstOrDefault();
        var shortened = frames < TransitionRules.DefaultFramesOf(typeId, FrameRate);
        Report(result, successMessage: string.Format(shortened ? Strings.Transition_AddedLongest : Strings.Transition_Added,
            TransitionNames.Name(typeId), TransitionNames.Duration(frames, FrameRate)));
    }

    /// <summary>
    /// "Dissolve": the track's default transition on the cut between the two selected clips — on a video track a cross
    /// dissolve by <see cref="AddTransition(string?)"/> (D044 §6), on an audio track the crossfade as before (D032 R9):
    /// <see cref="DefaultDissolveDuration"/>, or the longest the edit service says fits there when that is shorter. Every
    /// check is the service's; its message is shown when it refuses. The new transition is selected.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddDissolve))]
    private void AddDissolve()
    {
        if (DissolvePair() is not var (left, right)) return;
        var crossfade = left is AudioClip;
        if (!crossfade)
        {
            AddTransition(TransitionRules.CrossDissolve);
            return;
        }

        if (_edit.MaxTransitionFrames(left.Id, right.Id) is not { } max)
        {
            _status.Report(crossfade
                ? Strings.Dissolve_SelectAudio
                : Strings.Dissolve_SelectVideo);
            return;
        }

        var wanted = TransitionRules.Frames(DefaultDissolveDuration, FrameRate);
        var frames = max >= TransitionRules.MinFrames ? Math.Min(wanted, max) : wanted;
        var result = _edit.AddTransition(left.Id, right.Id, MediaTime.FromFrame(frames, FrameRate));
        if (result.Success && result.TransitionId is { } id)
            SelectTransition(id);
        var added = (crossfade, frames < wanted) switch
        {
            (true, true) => Strings.Crossfade_AddedLongest,
            (true, false) => Strings.Crossfade_Added,
            (false, true) => Strings.Dissolve_AddedLongest,
            (false, false) => Strings.Dissolve_Added
        };
        Report(result, successMessage: string.Format(added, Plural.Format(frames, Strings.Plural_Frames)));
    }

    // --- Ripple delete and close gap (D027 §2) -------------------------------------
    // The rules (which clips move, dissolves, locks, validation) are the edit service's; its message is shown when it
    // refuses.

    private bool CanRippleDelete() => CanEdit() && HasSelection;

    /// <summary>Close Gap works on the gap right before the one selected clip, on its track.</summary>
    private bool CanCloseGap() => CanEdit() && SelectionWithoutPrimaryPartner().Count == 1;

    /// <summary>"Ripple Delete": the selected clips go and the later clips of their tracks close up (D027 §2).</summary>
    [RelayCommand(CanExecute = nameof(CanRippleDelete))]
    private void RippleDelete()
    {
        var result = _edit.RippleDeleteClips(_selection.ToList());
        if (result.Success) ClearSelection();
        Report(result, successMessage: result.ClipIds.Count == 1 ? Strings.RippleDelete_One : Plural.Format(result.ClipIds.Count, Strings.RippleDelete_Many));
    }

    /// <summary>"Close Gap": the empty span before the selected clip goes; that clip and the later ones of its track move
    /// left (D027 §2). The selection stays.</summary>
    [RelayCommand(CanExecute = nameof(CanCloseGap))]
    private void CloseGap()
    {
        if (SelectionWithoutPrimaryPartner() is not [var only]) return;
        Report(_edit.CloseGapBefore(only), successMessage: Strings.Gap_Closed);
    }

    // --- Copy / paste / duplicate (D027 §5) ------------------------------------------
    // The clipboard is this panel's session state for the current project (cleared on New / Open / Recover): what the
    // edit service copied. Placement, tracks, media and overlap rules are the service's; its message is shown when it
    // refuses. Copy changes nothing, so it stays available during an export.

    /// <summary>The clips copied last (null = nothing copied in this project).</summary>
    public TimelineClipboard? Clipboard
    {
        get => _clipboard;
        private set
        {
            if (SetProperty(ref _clipboard, value))
                PasteCommand.NotifyCanExecuteChanged();
        }
    }
    private TimelineClipboard? _clipboard;

    private bool CanCopy() => HasSelection;
    private bool CanPaste() => CanEdit() && Clipboard is not null;
    private bool CanDuplicate() => CanEdit() && HasSelection;

    /// <summary>"Copy" (Ctrl+C): the selected clips.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (_edit.CopyClips(_selection.ToList()) is not { } copied)
        {
            _status.ReportWarning(Strings.Copy_NothingCopied);
            return;
        }
        Clipboard = copied;
        _status.Report(Plural.Format(copied.Count, Strings.Copy_Copied));
    }

    /// <summary>"Paste" (Ctrl+V): the copied clips at the playhead, on their tracks; the pasted clips are selected.</summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void Paste()
    {
        if (Clipboard is not { } clipboard) return;
        var result = _edit.PasteClips(clipboard, EditPlayhead);
        SelectAdded(result);
        Report(result, successMessage: Plural.Format(result.ClipIds.Count, Strings.Copy_Pasted));
    }

    /// <summary>"Duplicate" (Ctrl+D): copies of the selected clips right after them; the copies are selected.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void Duplicate()
    {
        var result = _edit.DuplicateClips(_selection.ToList());
        SelectAdded(result);
        Report(result, successMessage: Plural.Format(result.ClipIds.Count, Strings.Copy_Duplicated));
    }

    // --- Markers (D027 §6) --------------------------------------------------------------
    // The corner buttons above the track headers. Adding and removing change the project (undoable, not during an
    // export); going to a marker only moves the playhead. Which marker is where is the edit service's.

    /// <summary>"Add Marker": a marker at the playhead.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddMarker() => Report(_edit.AddMarker(EditPlayhead), successMessage: string.Format(Strings.Marker_Added, TimeFormat.ToTimecode(EditPlayhead, FrameRate)));

    /// <summary>"Remove Marker": the marker at the playhead (go to it first).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveMarker() => Report(_edit.RemoveMarkerAt(EditPlayhead), successMessage: Strings.Marker_Removed);

    [RelayCommand]
    private void PreviousMarker() => GoToMarker(_edit.PreviousMarker(Playhead), Strings.Marker_NoneBefore);

    [RelayCommand]
    private void NextMarker() => GoToMarker(_edit.NextMarker(Playhead), Strings.Marker_NoneAfter);

    /// <summary>D040 §2 (FU-21.1): a jump reports where it went, so an earlier refusal never stays in the status bar.</summary>
    private void GoToMarker(MediaTime? marker, string refusal)
    {
        if (marker is not { } at)
        {
            _status.ReportWarning(refusal);
            return;
        }
        SetPlayhead(at);
        _status.Report(string.Format(Strings.Marker_Reached, TimeFormat.ToTimecode(Playhead, FrameRate)));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))] private void AddVideoTrack() => Report(_edit.AddTrack(TrackType.Video));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void AddAudioTrack() => Report(_edit.AddTrack(TrackType.Audio));

    // --- Tracks (D027 §3) -------------------------------------------------------------
    // The header's ▲ / ▼ / ✕. Every rule (locks, the last track, the neighbour) is the edit service's; its message is
    // shown when it refuses.

    private bool CanMoveTrackUp(TimelineTrackViewModel? track) => CanEdit() && track is { HasTrackAbove: true };
    private bool CanMoveTrackDown(TimelineTrackViewModel? track) => CanEdit() && track is { HasTrackBelow: true };
    private bool CanDeleteTrack(TimelineTrackViewModel? track) => CanEdit() && track is not null;

    /// <summary>One place up in the timeline: video tracks are listed top layer first (a higher order), audio tracks by
    /// ascending order.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveTrackUp))]
    private void MoveTrackUp(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        Report(_edit.MoveTrack(track.Track.Id, track.Type == TrackType.Video ? 1 : -1), successMessage: string.Format(Strings.Track_MovedUp, track.Track.Name));
    }

    [RelayCommand(CanExecute = nameof(CanMoveTrackDown))]
    private void MoveTrackDown(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        Report(_edit.MoveTrack(track.Track.Id, track.Type == TrackType.Video ? -1 : 1), successMessage: string.Format(Strings.Track_MovedDown, track.Track.Name));
    }

    // --- Track state (D030 §4) ---------------------------------------------------------
    // The header's mute / hide / lock toggles: each click one undoable command; mute and hide also on a locked track
    // (D030 Q13); all disabled while an export runs.

    private bool CanToggleTrack(TimelineTrackViewModel? track) => CanEdit() && track is not null;
    private bool CanToggleTrackHidden(TimelineTrackViewModel? track) => CanToggleTrack(track) && track!.CanHide;

    [RelayCommand(CanExecute = nameof(CanToggleTrack))]
    private void ToggleTrackMute(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var muted = !track.Track.IsMuted;
        Report(_edit.SetTrackMuted(track.Track.Id, muted), successMessage: string.Format(muted ? Strings.Track_Muted : Strings.Track_Unmuted, track.Track.Name));
    }

    /// <summary>D064 §1: the header's S — solo on / off (one undo step, allowed on a locked track).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ToggleTrackSolo(TimelineTrackViewModel? track)
    {
        if (track?.Mix is not { } mix) return;
        mix.IsSolo = !mix.IsSolo;
    }

    [RelayCommand(CanExecute = nameof(CanToggleTrackHidden))]
    private void ToggleTrackHidden(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var hidden = !track.Track.IsHidden;
        Report(_edit.SetTrackHidden(track.Track.Id, hidden), successMessage: string.Format(hidden ? Strings.Track_Hidden : Strings.Track_Shown, track.Track.Name));
    }

    [RelayCommand(CanExecute = nameof(CanToggleTrack))]
    private void ToggleTrackLock(TimelineTrackViewModel? track)
    {
        if (track is null) return;
        var locked = !track.Track.IsLocked;
        Report(_edit.SetTrackLocked(track.Track.Id, locked), successMessage: string.Format(locked ? Strings.Track_Locked : Strings.Track_Unlocked, track.Track.Name));
    }

    /// <summary>Deletes a track; one with clips only after the user confirms (D027 §3). Without a dialog service a track
    /// with clips is not deleted.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteTrack))]
    private async Task DeleteTrack(TimelineTrackViewModel? track)
    {
        if (track is null || !CanEdit()) return;
        var name = track.Track.Name;
        var clipCount = track.Track.Clips.Count;
        if (_edit.GetDeleteTrackBlockReason(track.Track.Id) is { } blocked)
        {
            _status.ReportWarning(blocked);
            return;
        }
        if (clipCount > 0)
        {
            if (_dialogs is null) return;
            var choice = await _dialogs.AskAsync(new DialogRequest
            {
                Title = Strings.Track_DeleteTitle,
                Message = clipCount == 1
                    ? string.Format(Strings.Track_DeleteOne, name)
                    : Plural.Format(clipCount, Strings.Track_DeleteMany, name),
                Buttons = new[] { Strings.Track_DeleteButton, Strings.Common_Cancel }
            });
            // The answer may come after an export started or the project was replaced; the service re-checks the rest.
            if (choice != 0 || !CanEdit()) return;
        }

        Report(_edit.DeleteTrack(track.Track.Id), successMessage: string.Format(Strings.Track_Deleted, name));
    }

    [RelayCommand] private void ToggleMagnet() => MagneticMainTrack = !MagneticMainTrack;

    private readonly ITextBoxMeasurer? _textBoxes;

    /// <summary>"+ Text": a text clip at the playhead on the topmost video track, selected.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddText()
    {
        // D043 §4: placed at the bottom centre by its box (centred when no measurer is given, e.g. without a platform).
        var result = _edit.AddTextClip(EditPlayhead, _textBoxes is { } boxes ? boxes.Measure : null);
        SelectAdded(result);
        Report(result, successMessage: Strings.Text_Added);
    }

    /// <summary>«Шаблон ▾» (D045 §5): a template of the kind named by <paramref name="kind"/> ("Rec", "Vhs", "Boulder",
    /// "Highlight", "HeartRate") at the playhead on the topmost video track, selected.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task AddTemplate(string kind)
    {
        if (!Enum.TryParse<TemplateKind>(kind, out var parsed)) return;
        var at = EditPlayhead;
        // D069 §5: a heart rate plate where the training has no samples would show nothing — it is not created, and a window
        // with «ОК» says why (the status bar's warning was not noticed).
        if (parsed == TemplateKind.HeartRate && !HasHeartRateAt(at))
        {
            _status.ReportWarning(Strings.Template_NoHeartRateHere);
            if (_dialogs is not null)
                await _dialogs.AskAsync(new DialogRequest
                {
                    Title = Strings.Template_NoHeartRateTitle,
                    Message = Strings.Template_NoHeartRateHere,
                    Buttons = new[] { Strings.Common_OK }
                });
            return;
        }
        var result = _edit.AddTemplateClip(parsed, at);
        SelectAdded(result);
        Report(result, successMessage: Strings.Template_Added);
    }

    /// <summary>D068 amendment 1: «Пульс» is offered only with a usable training (it shows nothing without one).</summary>
    public bool HasTraining => _projectService.Current.Training is { IsUsable: true };

    /// <summary>The template menu is opening: its «Пульс» follows the training.</summary>
    public void RefreshTemplateMenu() => OnPropertyChanged(nameof(HasTraining));

    /// <summary>Whether the training has a heart rate for the picture of the topmost video clip at <paramref name="at"/>
    /// (D055 §1: the file's time and the training's clock correction).</summary>
    internal bool HasHeartRateAt(MediaTime at)
    {
        if (_projectService.Current.Training is not { Workout: { } workout } training) return false;
        foreach (var track in Sequence.VideoTracks.OrderByDescending(t => t.Order))
        {
            if (track.Clips.OfType<VideoClip>().FirstOrDefault(c => c.TimelineStart <= at && at < c.TimelineEnd) is not { } clip) continue;
            if (FindAsset(clip.MediaAssetId) is not { } asset || FitRouteBinding.FileTimeOf(asset, workout) is not { Zero: var zero }) continue;
            var offset = at - clip.TimelineStart;
            var source = new MediaTime(clip.SourceIn.Ticks + offset.Ticks * clip.Speed.Numerator / clip.Speed.Denominator);
            if (TrainingAlignment.HeartRateAtSource(workout, zero, training.ClockCorrection, source) is not null) return true;
        }
        return false;
    }

    /// <summary>"Add to Timeline" from the Media Browser: end of V1 / A1.</summary>
    public void AddMedia(MediaAsset asset) => AddMediaAt(asset.Id, null, null);

    /// <summary>Drag-and-drop from the Media Browser onto a track position (snapped, D037 §2).</summary>
    public void DropMedia(Guid assetId, TimelineTrackViewModel? track, double contentX)
    {
        var start = DropStart(assetId, track, contentX);
        ClearDropTargets();
        AddMediaAt(assetId, track?.Track.Id, start, insertOnDrop: true);
    }

    /// <summary>
    /// The owner's acceptance of 2026-11-10: several files dragged from the Media Browser and dropped on a track — in their
    /// order of recording, the first at the drop's place (D057 §3.12), the next ones right after it; one Undo step.
    /// </summary>
    public void DropMediaMany(IReadOnlyList<Guid> assetIds, TimelineTrackViewModel? track, double contentX)
    {
        var ordered = OrderedForDrop(assetIds);
        if (ordered.Count <= 1)
        {
            if (ordered.Count == 1) DropMedia(ordered[0], track, contentX);
            else ClearDropTargets();
            return;
        }
        var start = DropStart(ordered[0], track, contentX);
        ClearDropTargets();
        if (!CanEdit()) return;
        var wasEmpty = IsTimelineEmpty;
        var result = track is null ? _edit.AddClips(ordered) : _edit.AddClips(ordered, track.Track.Id, start, insertOnDrop: true);
        SelectAdded(result);
        Report(result, successMessage: string.Format(Strings.Timeline_AddedMany, result.ClipIds.Count));
        OfferCanvasAfterAdd(wasEmpty, result);
    }

    /// <summary>The ghost of a drag of <paramref name="assetIds"/> over <paramref name="track"/> (one file or several).</summary>
    public void ShowDropPreview(IReadOnlyList<Guid> assetIds, TimelineTrackViewModel? track, double contentX)
    {
        SuspendFollowing();   // D058 §1: a drop being placed while playing
        var ordered = OrderedForDrop(assetIds);
        if (ordered.Count == 0)
        {
            ShowDropTarget(track);
            return;
        }
        ShowDropPreview(ordered[0], track, contentX);
        var start = DropStart(ordered[0], track, contentX);
        ShowDropGhost(track, track is null ? null : _edit.PreviewAddClips(ordered, track.Track.Id, start, insertOnDrop: true));
    }

    private IReadOnlyList<Guid> OrderedForDrop(IReadOnlyList<Guid> assetIds)
    {
        var project = _projectService.Current;
        var assets = assetIds.Select(id => project.MediaAssets.FirstOrDefault(a => a.Id == id)).OfType<MediaAsset>().ToList();
        return MediaRecordingOrder.Order(assets, project.Training?.Workout?.UtcOffset).Select(a => a.Id).ToList();
    }

    private void ShowDropGhost(TimelineTrackViewModel? track, DropPreview? preview)
    {
        foreach (var t in Tracks)
        {
            if (t == track && preview is { } p)
            {
                t.DropGhostLeft = TimelineCoordinateMapper.TimeToX(p.Start, PixelsPerSecond);
                t.DropGhostWidth = Math.Max(2, TimelineCoordinateMapper.TimeToX(p.End - p.Start, PixelsPerSecond));
                t.DropGhostMovesOthers = p.MovesOthers;
                t.DropGhostInvalid = false;
                t.HasDropGhost = true;
            }
            else t.HasDropGhost = false;
        }
    }

    /// <summary>While media is dragged over the lanes: the target track and the snap indicator where it would land — on a
    /// clip, right after it (D057 §3.12).</summary>
    public void ShowDropPreview(Guid assetId, TimelineTrackViewModel? track, double contentX)
    {
        SuspendFollowing();   // D058 §1
        ShowDropTarget(track);
        var start = DropStart(assetId, track, contentX);
        if (track?.Clips.FirstOrDefault(c => c.Clip.TimelineStart < start && start < c.Clip.TimelineEnd) is { } under)
        {
            SnapIndicatorX = TimelineCoordinateMapper.TimeToX(under.Clip.TimelineEnd, PixelsPerSecond);
            IsSnapIndicatorVisible = true;
        }
    }

    private string? _lastDragRefusal;

    /// <summary>D057 §3.12: a drag the release would refuse says why while it is shown, once per reason.</summary>
    private void ReportDragRefusal(string? reason)
    {
        if (reason == _lastDragRefusal) return;
        _lastDragRefusal = reason;
        if (reason is not null) _status.ReportWarning(reason);
    }

    /// <summary>Where a drop at <paramref name="contentX"/> puts the new clip (D037 §2): with Snap on, its start or end onto the
    /// nearest target within the tolerance that makes no overlap the pointer's own place would not — on an empty track too
    /// (D071 amendment 1 replaced Q8's «at 0»); with Snap off exactly under the pointer. Shows the snap indicator.</summary>
    private MediaTime DropStart(Guid assetId, TimelineTrackViewModel? track, double contentX)
    {
        var raw = TimelineCoordinateMapper.XToFrameTime(contentX, PixelsPerSecond, FrameRate);
        IsSnapIndicatorVisible = false;
        if (!SnappingEnabled || FindAsset(assetId) is not { } asset) return raw;

        var target = track?.Track ?? (asset.Kind == MediaKind.Audio ? Sequence.AudioTracks : Sequence.VideoTracks).FirstOrDefault();
        if (target is null) return raw;
        // D071 amendment 1 (owner, 2026-11-19) replaces D037 Q8: a drop onto an empty track lands under the pointer, as on any
        // other track (music dropped onto an empty audio track no longer jumps to 0); the magnetic main track still closes up.

        var duration = _edit.AddedClipDuration(assetId) ?? MediaTime.Zero;
        bool Overlaps(MediaTime start) => target.Clips.Any(c => c.TimelineStart < start + duration && c.TimelineEnd > start);
        var rawOverlaps = Overlaps(raw);
        var candidates = duration > MediaTime.Zero ? new[] { raw, raw + duration } : new[] { raw };
        return raw + ApplySnap(candidates, Array.Empty<TimelineClipViewModel>(), MediaTime.Zero,
            accept: rawOverlaps ? null : s => !Overlaps(raw + s.Offset));
    }

    private void AddMediaAt(Guid assetId, Guid? trackId, MediaTime? start, bool insertOnDrop = false)
    {
        if (!CanEdit()) return;
        var wasEmpty = IsTimelineEmpty;
        var result = _edit.AddClip(assetId, trackId, start, insertOnDrop);
        SelectAdded(result);
        Report(result, successMessage: Strings.Timeline_Added);
        OfferCanvasAfterAdd(wasEmpty, result);
    }

    /// <summary>D057 §3.3: the clip menu's «Разметить зацепки» and «Распознать прохождения из FIT» — the hold markup's and the
    /// Inspector's commands, set by the shell (the timeline owns neither).</summary>
    public System.Windows.Input.ICommand? MarkHoldsCommand { get; set; }

    public System.Windows.Input.ICommand? RecognizeFitClimbsCommand { get; set; }

    /// <summary>D057 §3.2: several files of the Media Browser, already in the order they go (their recording order) —
    /// one edit, one Undo step.</summary>
    public void AddMediaMany(IReadOnlyList<MediaAsset> assets)
    {
        if (!CanEdit() || assets.Count == 0) return;
        var wasEmpty = IsTimelineEmpty;
        var result = _edit.AddClips(assets.Select(a => a.Id).ToList());
        SelectAdded(result);
        Report(result, successMessage: string.Format(Strings.Timeline_AddedMany, result.ClipIds.Count));
        OfferCanvasAfterAdd(wasEmpty, result);
    }

    private void SelectAdded(TimelineEditResult result)
    {
        if (!result.Success || result.ClipIds.Count == 0) return;
        DropTransitionSelection();
        DropOrange();
        _selection.Clear();
        _selection.AddRange(result.ClipIds);
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    public void ShowDropTarget(TimelineTrackViewModel? track)
    {
        foreach (var t in Tracks) t.IsDropTarget = t == track;
    }

    public void ClearDropTargets()
    {
        foreach (var t in Tracks) t.HasDropGhost = false;
        ShowDropTarget(null);
        IsSnapIndicatorVisible = false;
    }

    private void Report(TimelineEditResult result, string? successMessage = null)
    {
        if (!result.Success)
            _status.ReportWarning(result.Message ?? Strings.Timeline_EditNotPossible);
        else if (result.Message is not null)
            _status.Report(result.Message);
        else if (successMessage is not null && !result.NoChange)
            _status.Report(successMessage);
    }
}
