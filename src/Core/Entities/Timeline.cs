using AiVideoEditor.Core.Common;

namespace AiVideoEditor.Core.Entities;

public enum TrackType
{
    Video,
    Audio
}

/// <summary>A single horizontal lane on the timeline holding non-overlapping clips.</summary>
public sealed class Track
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TrackType Type { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Rendering/mixing order: for video, higher index composites on top.</summary>
    public int Order { get; set; }

    public bool IsMuted { get; set; }
    public bool IsHidden { get; set; }
    public bool IsLocked { get; set; }

    public List<Clip> Clips { get; } = new();
    public List<Transition> Transitions { get; } = new();
}

/// <summary>
/// The editable sequence of tracks that the user assembles. A <see cref="Project"/>
/// currently holds a single top-level <see cref="Sequence"/> (the "Timeline"); the
/// distinction is kept so that nested/multi-sequence editing can be added later
/// without reshaping the model.
/// </summary>
public sealed class Sequence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Main Sequence";

    public List<Track> VideoTracks { get; } = new();
    public List<Track> AudioTracks { get; } = new();

    public List<Marker> Markers { get; } = new();

    /// <summary>Current playhead position, independent of any UI frame rate.</summary>
    public MediaTime PlayheadPosition { get; set; }

    /// <summary>Current timeline zoom, expressed as pixels-per-second in the UI. Stored here
    /// so it survives project save/reload as part of the editing session.</summary>
    public double ZoomPixelsPerSecond { get; set; } = 60;

    public bool SnappingEnabled { get; set; } = true;

    public MediaTime Duration()
    {
        var end = MediaTime.Zero;
        foreach (var track in VideoTracks.Concat(AudioTracks))
        {
            foreach (var clip in track.Clips)
            {
                if (clip.TimelineEnd > end) end = clip.TimelineEnd;
            }
        }
        return end;
    }
}
