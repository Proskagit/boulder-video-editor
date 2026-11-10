using AiVideoEditor.Core.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiVideoEditor.UI.ViewModels.Panels;

// --- D058 §4: a marker's label, edited in place by a double click on its flag ---------------------------------------------
public partial class TimelineViewModel
{
    /// <summary>How far from a flag (in pixels) a double click on the ruler still means that marker.</summary>
    public const double MarkerHitPixels = 6;

    /// <summary>The marker whose label is being edited, or null.</summary>
    private Guid? _editingMarkerId;

    [ObservableProperty] private bool _isEditingMarkerLabel;
    [ObservableProperty] private double _markerLabelLeft;
    [ObservableProperty] private string _markerLabelText = "";

    public int MaxMarkerLabelLength => ITimelineEditService.MaxMarkerLabelLength;

    /// <summary>The marker nearest <paramref name="contentX"/> within <see cref="MarkerHitPixels"/>, or null.</summary>
    public TimelineMarkerViewModel? MarkerAt(double contentX) =>
        Markers.Where(m => Math.Abs(m.Left - contentX) <= MarkerHitPixels).OrderBy(m => Math.Abs(m.Left - contentX)).FirstOrDefault();

    /// <summary>A double click on the ruler: on a marker, its label opens for editing; true when it did.</summary>
    public bool BeginMarkerLabelEdit(double contentX)
    {
        if (!IsEditingAllowed || MarkerAt(contentX) is not { } marker) return false;
        _editingMarkerId = marker.Id;
        MarkerLabelLeft = marker.Left;
        MarkerLabelText = Sequence.Markers.FirstOrDefault(m => m.Id == marker.Id)?.Label ?? "";
        IsEditingMarkerLabel = true;
        return true;
    }

    /// <summary>Enter or leaving the field: the label is set (one undo step).</summary>
    [RelayCommand]
    private void CommitMarkerLabel()
    {
        if (_editingMarkerId is not { } id) return;
        _editingMarkerId = null;
        IsEditingMarkerLabel = false;
        var result = _edit.SetMarkerLabel(id, MarkerLabelText);
        Report(result);
    }

    /// <summary>Esc: the label stays as it was.</summary>
    [RelayCommand]
    private void CancelMarkerLabel()
    {
        _editingMarkerId = null;
        IsEditingMarkerLabel = false;
    }
}
