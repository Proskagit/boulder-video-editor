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

/// <summary>The selection (split out of TimelineViewModel.cs, D075 amendment 1 — no change).</summary>
public sealed partial class TimelineViewModel
{
    // --- Selection ----------------------------------------------------------------

    /// <summary>Pointer pressed on a clip. Ctrl toggles it in the selection; a plain
    /// press on an unselected clip selects only that clip. Returns false when the press
    /// must not start a drag (Ctrl-toggle).</summary>
    public bool OnClipPressed(TimelineClipViewModel clip, bool toggle)
    {
        _rangeAnchor = clip.Id;                // D057 §3.5: a Shift+click selects the range from here
        DropTransitionSelection();
        SelectOrange(null, null);
        if (toggle)
        {
            // D032 AQ1: Ctrl toggles a linked clip together with its partner.
            var group = SelectionGroup(clip.Id);
            if (_selection.Contains(clip.Id)) _selection.RemoveAll(group.Contains);
            else _selection.AddRange(group.Where(id => !_selection.Contains(id)));
            UpdateSelectionVisuals();
            RaiseSelectionChanged();
            return false;
        }

        if (!_selection.Contains(clip.Id))
            SelectOnly(clip.Id);
        return true;
    }

    /// <summary>A press on a clip's picture in the Preview with a tool on (D038 §2): the clip is selected as a click on it
    /// here would select it (with its link partner) unless it already is the primary.</summary>
    public void SelectClipFromPreview(Guid id)
    {
        if (!_clipViewModels.ContainsKey(id) || (_selection.Count > 0 && _selection[^1] == id && _selectedTransitionId is null)) return;
        SelectOnly(id);
    }

    /// <summary>Pointer released on a clip without dragging: collapse a multi-selection to it.</summary>
    public void OnClipClicked(TimelineClipViewModel clip, bool toggle)
    {
        if (!toggle && !_selection.SequenceEqual(SelectionGroup(clip.Id)))
            SelectOnly(clip.Id);
    }

    /// <summary>Pointer pressed on a dissolve's zone: it becomes the selection (no clip stays selected).</summary>
    public void OnTransitionPressed(TimelineTransitionViewModel transition) => SelectTransition(transition.Id);

    /// <summary>The dissolve zone of <paramref name="track"/> at <paramref name="contentX"/>, if any. The view asks this
    /// only when no trim handle was hit: the clips' handles take precedence over a zone drawn over them.</summary>
    public TimelineTransitionViewModel? TransitionAt(TimelineTrackViewModel? track, double contentX) =>
        track?.Transitions.FirstOrDefault(t => t.Contains(contentX));

    private void SelectTransition(Guid id)
    {
        DropOrange();
        if (_selection.Count > 0)
        {
            _selection.Clear();
            UpdateSelectionVisuals();
            RaiseSelectionChanged();
        }
        _selectedTransitionId = id;
        UpdateSelectionVisuals();
        RaiseTransitionSelectionChanged();
    }

    /// <summary>Drops a dissolve selection (a clip or nothing is being selected instead).</summary>
    private void DropTransitionSelection()
    {
        if (_selectedTransitionId is null) return;
        _selectedTransitionId = null;
        UpdateSelectionVisuals();
        RaiseTransitionSelectionChanged();
    }

    private void RaiseTransitionSelectionChanged()
    {
        if (_selectedTransitionId is { } id && _transitionViewModels.TryGetValue(id, out var vm))
        {
            var t = vm.Transition;
            var left = _clipViewModels.TryGetValue(t.LeftClipId, out var a) ? a.Name : "";
            var right = _clipViewModels.TryGetValue(t.RightClipId, out var b) ? b.Name : "";
            TransitionSelectionChanged?.Invoke(this, new TimelineTransitionSelection(t, vm.Track, left, right, FrameRate,
                _edit.MaxTransitionFrames(t.LeftClipId, t.RightClipId)));
        }
        else
        {
            TransitionSelectionChanged?.Invoke(this, null);
        }
    }

    public void ClearSelection()
    {
        DropTransitionSelection();
        if (_selection.Count == 0) return;
        _selection.Clear();
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    /// <summary>Clears the selection without notifying (used when another panel's
    /// selection takes over the Inspector).</summary>
    public void ClearSelectionSilently()
    {
        DropOrange();   // D062 amendment 1: another panel takes the Inspector — the orange ends too
        _selection.Clear();
        _selectedTransitionId = null;
        UpdateSelectionVisuals();
    }

    /// <summary>Selects <paramref name="id"/> alone — with its link partner (D032 AQ1) unless <paramref name="withPartner"/>
    /// is false; the clip itself stays the primary (the last entry).</summary>
    private void SelectOnly(Guid id, bool withPartner = true)
    {
        DropTransitionSelection();
        DropOrange();
        _selection.Clear();
        _selection.AddRange(withPartner ? SelectionGroup(id) : new[] { id });
        UpdateSelectionVisuals();
        RaiseSelectionChanged();
    }

    /// <summary>What selecting <paramref name="id"/> selects: its link partner first, then the clip (D032 AQ1).</summary>
    private List<Guid> SelectionGroup(Guid id) =>
        _clipViewModels.TryGetValue(id, out var vm) && PartnerViewModel(vm) is { } partner
            ? new List<Guid> { partner.Id, id }
            : new List<Guid> { id };

    /// <summary>The view model of <paramref name="clip"/>'s link partner, or null.</summary>
    private TimelineClipViewModel? PartnerViewModel(TimelineClipViewModel clip) =>
        clip.Clip is MediaBackedClip { LinkId: { } link }
            ? _clipViewModels.Values.FirstOrDefault(v => v != clip && v.Clip is MediaBackedClip { LinkId: { } other } && other == link)
            : null;

    /// <summary>The selection without the primary's link partner: the clips the user picked (a linked pair counts once
    /// for the commands that need one or two clips).</summary>
    private List<Guid> SelectionWithoutPrimaryPartner()
    {
        if (_selection.Count == 0 || !_clipViewModels.TryGetValue(_selection[^1], out var primary)) return _selection.ToList();
        var partner = PartnerViewModel(primary);
        return _selection.Where(id => id != partner?.Id).ToList();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var vm in _clipViewModels.Values)
            vm.IsSelected = _selection.Contains(vm.Id);
        // D063: a keyframe stays selected only on the primary selected clip.
        if (SelectedKeyframe is { } keyframe && (_selection.Count == 0 || _selection[^1] != keyframe.Owner.ClipId)) SelectedKeyframe = null;
        NotifyKeyframeCommands();
        foreach (var vm in _transitionViewModels.Values)
            vm.IsSelected = vm.Id == _selectedTransitionId;
        HasSelection = _selection.Count > 0;
        HasTransitionSelection = _selectedTransitionId is not null;
        // Dissolve and Close Gap depend on how many clips are selected, not only on whether any is: 1 → 2 keeps
        // HasSelection true.
        AddDissolveCommand.NotifyCanExecuteChanged();
        AddTransitionCommand.NotifyCanExecuteChanged();
        OpenTransitionMenuCommand.NotifyCanExecuteChanged();
        CloseGapCommand.NotifyCanExecuteChanged();
        NotifyLinkCommands();
    }

    private void RaiseSelectionChanged()
    {
        if (_selection.Count > 0 && _clipViewModels.TryGetValue(_selection[^1], out var primary))
        {
            var asset = primary.Clip is MediaBackedClip m ? FindAsset(m.MediaAssetId) : null;
            var (dissolveAtStart, dissolveAtEnd) = Dissolves(primary.Clip);
            var settings = _projectService.Current.Settings;
            SelectionChanged?.Invoke(this, new TimelineClipSelection(primary.Clip, primary.Name, asset, FrameRate, dissolveAtStart, dissolveAtEnd,
                _selection.Count, new FrameSize(settings.FrameWidth, settings.FrameHeight))
            {
                Selected = _selection.Where(_clipViewModels.ContainsKey).Select(id => _clipViewModels[id].Clip).ToList(),
                FindAsset = FindAsset,
                Anchor = primary.Clip is TemplateClip { AnchorClipId: { } anchorId } && _clipViewModels.TryGetValue(anchorId, out var anchor)
                    ? anchor.Clip as VideoClip : null,
                AnchorRouteId = (primary.Clip as TemplateClip)?.AnchorRouteId,
                Sequence = Sequence
            });
        }
        else if (SelectedOrangeClipId is { } orangeClip && _selectedOrangeRouteId is { } orangeRoute &&
                 _clipViewModels.TryGetValue(orangeClip, out var owner) && owner.Clip is VideoClip video)
        {
            // D062 amendment 1: the orange keeps the Inspector on its video and route (its holds), not empty.
            var settings = _projectService.Current.Settings;
            SelectionChanged?.Invoke(this, new TimelineClipSelection(video, owner.Name, FindAsset(video.MediaAssetId), FrameRate,
                Canvas: new FrameSize(settings.FrameWidth, settings.FrameHeight))
            {
                FindAsset = FindAsset,
                OrangeRouteId = orangeRoute
            });
        }
        else
        {
            SelectionChanged?.Invoke(this, null);
        }
    }
}
