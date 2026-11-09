using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>A FIT mark on a clip (D051 §8.1): its frame in pixels from the clip's left edge, its colour by kind, its
/// tooltip; editor only — never in Preview or export.</summary>
public sealed record FitMarkGlyph(double Left, string ColorHex, string Tip, ClimbIdentity Climb, bool IsSelected);

/// <summary>
/// Phase 37 (D051 §8.1–§8.2) on the timeline: every FIT mark drawn on each clip of its video that shows its frame — blue
/// as proposed, amber on the edge of the recording, violet when moved, outlined when linked to an animation. A click
/// selects a mark; Delete then hides it (declines its climb, one Undo step — §5.7). The other actions are in the
/// Inspector and the COROS menu.
/// </summary>
public sealed partial class TimelineViewModel
{
    public const string FitProposedHex = "#29B6F6";
    public const string FitClampedHex = "#FFCA28";
    public const string FitMovedHex = "#BA68C8";

    private ClimbIdentity? _selectedFitMark;

    /// <summary>The FIT mark actions, when the edit service has them.</summary>
    private IFitMarkEditService? FitMarks => _edit as IFitMarkEditService;

    /// <summary>The selected FIT mark, or null.</summary>
    public ClimbIdentity? SelectedFitMark
    {
        get => _selectedFitMark;
        set
        {
            if (_selectedFitMark == value) return;
            _selectedFitMark = value;
            OnPropertyChanged();
            RefreshFitMarks();
            DeleteSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A click on a mark: selects it (and nothing else).</summary>
    public void OnFitMarkClicked(ClimbIdentity climb)
    {
        ClearSelection();
        SelectOrange(null, null);
        SelectedFitMark = climb;
    }

    private void RefreshFitMarks()
    {
        var project = _projectService.Current;
        var marks = project.Training?.Marks ?? [];
        if (_selectedFitMark is { } selected && marks.All(m => m.Climb != selected))
        {
            _selectedFitMark = null;
            OnPropertyChanged(nameof(SelectedFitMark));
        }
        foreach (var vm in _clipViewModels.Values)
        {
            if (vm.Clip is not VideoClip clip || marks.Count == 0)
            {
                vm.FitMarks = Array.Empty<FitMarkGlyph>();
                continue;
            }
            var glyphs = new List<FitMarkGlyph>();
            var start = clip.TimelineStart.ToNearestFrame(FrameRate);
            var end = clip.TimelineEnd.ToNearestFrame(FrameRate);
            foreach (var mark in marks.Where(m => m.MediaAssetId == clip.MediaAssetId))
            {
                if (FitMarkRules.EffectiveStart(mark with { LinkedClipId = null }, project)?.SourceTime is not { } source) continue;
                var frame = HoldAnimationRule.StartFrame(clip.TimelineStart, clip.SourceIn, clip.Speed, source, FrameRate);
                if (frame < start || frame >= end) continue;
                var left = TimelineCoordinateMapper.TimeToX(MediaTime.FromFrame(frame, FrameRate) - clip.TimelineStart, PixelsPerSecond);
                glyphs.Add(new FitMarkGlyph(left, ColorOf(mark), TipOf(mark, project), mark.Climb, mark.Climb == _selectedFitMark));
            }
            vm.FitMarks = glyphs;
        }
    }

    private static string ColorOf(FitMark mark) => mark.Position.Kind switch
    {
        FitMarkPositionKind.Clamped => FitClampedHex,
        FitMarkPositionKind.Moved => FitMovedHex,
        _ => FitProposedHex
    };

    private static string TipOf(FitMark mark, Project project)
    {
        var kind = mark.Position.Kind switch
        {
            FitMarkPositionKind.Clamped => Strings.FitMark_Clamped,
            FitMarkPositionKind.Moved => Strings.FitMark_Moved,
            _ => Strings.FitMark_Proposed
        };
        var text = string.Format(Strings.FitMark_Tip, mark.Climb.ClimbNumber, kind);
        return mark.LinkedClipId is null ? text : text + Strings.FitMark_TipLinked;
    }

    private void DeclineSelectedFitMark(ClimbIdentity climb)
    {
        if (FitMarks is not { } marks) return;
        var result = marks.DeclineClimb(climb);
        if (result.Success) SelectedFitMark = null;
        Report(result, successMessage: Strings.FitMark_Declined);
    }
}
