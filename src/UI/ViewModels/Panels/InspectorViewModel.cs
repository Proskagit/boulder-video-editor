using AiVideoEditor.Core.Common;
using AiVideoEditor.UI.Common;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Right-hand Inspector. Phase 1 always shows one mock "selected" clip's
/// properties so the panel's layout can be built and reviewed; there is no real
/// selection concept wired up yet (that needs the Timeline's selection model,
/// Phase 3). The Transform properties are editable now (plain view-model state)
/// so the panel already behaves like a real property sheet — they just don't
/// feed back into any clip yet.
/// </summary>
public sealed partial class InspectorViewModel : ViewModelBase
{
    // --- Transform (mock) -----------------------------------------------------
    // decimal, not double: Avalonia's NumericUpDown.Value is a nullable decimal,
    // and binding a double straight to it throws at runtime. Core's Clip.PositionX
    // etc. are double (right type for a transform/render pipeline) — Phase 6 will
    // do the decimal<->double conversion at the boundary when this actually wires
    // up to a real selected clip.
    [ObservableProperty] private decimal _positionX = 0;
    [ObservableProperty] private decimal _positionY = 0;
    [ObservableProperty] private decimal _scale = 1.0m;
    [ObservableProperty] private decimal _rotation = 0;
    [ObservableProperty] private decimal _opacity = 1.0m;

    // --- Clip (mock, read-only for now) -------------------------------------
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTimeDisplay))]
    private MediaTime _startTime = MediaTime.FromSeconds(8);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndTimeDisplay))]
    private MediaTime _endTime = MediaTime.FromSeconds(23);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    private MediaTime _duration = MediaTime.FromSeconds(15);

    public string StartTimeDisplay => TimeFormat.ToShortString(StartTime);
    public string EndTimeDisplay => TimeFormat.ToShortString(EndTime);
    public string DurationDisplay => TimeFormat.ToShortString(Duration);

    public string SelectedClipName => "interview_a.mov (mock selection)";
}
