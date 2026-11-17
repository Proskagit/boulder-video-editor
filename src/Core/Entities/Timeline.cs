using AiVideoEditor.Core.Audio;
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

    /// <summary>D064 §1: the track's volume (linear 0–2) — of its clips' sound (a video track's: its clips' own sound).</summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>D064 §1: while any track is solo only the solo tracks sound (the Preview and the export); monitoring state like
    /// mute.</summary>
    public bool IsSolo { get; set; }

    /// <summary>D064 §4: the track is lowered while speech is detected on another track; null — off.</summary>
    public DuckingSettings? Ducking { get; set; }

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
    public double ZoomPixelsPerSecond { get; set; } = DefaultZoomPixelsPerSecond;

    /// <summary>The zoom of a new sequence. D058 §2: a project still at it (the zoom never changed) is shown with the
    /// timeline's initial zoom (about ten minutes across) once the view knows its width.</summary>
    public const double DefaultZoomPixelsPerSecond = 60;

    public bool SnappingEnabled { get; set; } = true;

    /// <summary>D064 §1: the project's master volume (linear 0–2).</summary>
    public double MasterVolume { get; set; } = 1.0;

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
