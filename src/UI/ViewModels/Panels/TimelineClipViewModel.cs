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

    public TimelineTrackViewModel(Track track) => Track = track;

    public Track Track { get; }
    public string Label => Track.IsLocked ? $"{Track.Name} 🔒" : Track.Name;
    public TrackType Type => Track.Type;
    public ObservableCollection<TimelineClipViewModel> Clips { get; } = new();

    /// <summary>Highlighted while a drag/drop would land on this track.</summary>
    [ObservableProperty] private bool _isDropTarget;
}

/// <summary>A single labeled tick on the time ruler.</summary>
public sealed class TimelineRulerTickViewModel : ViewModelBase
{
    public required string Label { get; init; }
    public required double Left { get; init; }
}

/// <summary>The primary selected timeline clip, as passed to the Inspector.</summary>
public sealed record TimelineClipSelection(Clip Clip, string Name, MediaAsset? Asset, FrameRate Rate);
