using System.Collections.ObjectModel;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// One clip's visual rectangle on the timeline. <see cref="Left"/>/<see cref="Width"/>
/// are plain pixels for Phase 1's mock layout; from Phase 3 onward these are
/// computed from the real <see cref="Clip.TimelineStart"/>/<see cref="Clip.Duration"/>
/// via the timeline's pixels-per-second zoom factor instead of being hand-set.
/// </summary>
public sealed class TimelineClipViewModel : ViewModelBase
{
    public required string Name { get; init; }
    public required double Left { get; init; }
    public required double Width { get; init; }
    public required string ColorHex { get; init; }
}

/// <summary>One horizontal track lane (a video or audio row) with its mock clips.</summary>
public sealed class TimelineTrackViewModel : ViewModelBase
{
    public required string Label { get; init; }
    public required TrackType Type { get; init; }
    public ObservableCollection<TimelineClipViewModel> Clips { get; } = new();
}

/// <summary>A single labeled tick on the time ruler.</summary>
public sealed class TimelineRulerTickViewModel : ViewModelBase
{
    public required string Label { get; init; }
    public required double Left { get; init; }
}
