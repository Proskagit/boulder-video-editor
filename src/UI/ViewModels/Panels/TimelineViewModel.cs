using System.Collections.ObjectModel;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.UI.ViewModels.Panels;

/// <summary>
/// Bottom Timeline panel. Everything here — tracks, clips, ruler ticks, playhead
/// position — is mock geometry built once in the constructor, isolated from real
/// editing state on purpose: Phase 4 replaces this construction with a projection
/// over the real <see cref="Core.Entities.Sequence"/> (tracks/clips) plus the
/// zoom-driven pixel math, without needing to touch the view that binds to it.
/// No move/trim/split/snap behavior exists yet — this is visual only.
/// </summary>
public sealed class TimelineViewModel : ViewModelBase
{
    /// <summary>Mock zoom level (pixels per second) — stands in for
    /// <see cref="Core.Entities.Sequence.ZoomPixelsPerSecond"/> until a real Sequence exists.</summary>
    public double PixelsPerSecond { get; } = 40;

    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = new();
    public ObservableCollection<TimelineRulerTickViewModel> RulerTicks { get; } = new();

    /// <summary>Mock playhead X position in pixels.</summary>
    public double PlayheadLeft { get; } = 150;

    /// <summary>Total mock timeline width in pixels, wide enough to hold every mock clip and tick.</summary>
    public double ContentWidth { get; } = 2400;

    public TimelineViewModel()
    {
        BuildMockRuler();
        BuildMockTracks();
    }

    private void BuildMockRuler()
    {
        // One tick every 5 seconds for a 60-second mock timeline.
        for (var seconds = 0; seconds <= 60; seconds += 5)
        {
            RulerTicks.Add(new TimelineRulerTickViewModel
            {
                Label = $"{seconds / 60}:{seconds % 60:D2}",
                Left = seconds * PixelsPerSecond
            });
        }
    }

    private void BuildMockTracks()
    {
        var v2 = new TimelineTrackViewModel { Label = "V2", Type = TrackType.Video };
        v2.Clips.Add(new TimelineClipViewModel { Name = "logo.png", Left = 300, Width = 200, ColorHex = "#78703A" });
        Tracks.Add(v2);

        var v1 = new TimelineTrackViewModel { Label = "V1", Type = TrackType.Video };
        v1.Clips.Add(new TimelineClipViewModel { Name = "beach_sunset.mp4", Left = 0, Width = 320, ColorHex = "#3A5A78" });
        v1.Clips.Add(new TimelineClipViewModel { Name = "interview_a.mov", Left = 340, Width = 260, ColorHex = "#3A5A78" });
        Tracks.Add(v1);

        var a1 = new TimelineTrackViewModel { Label = "A1", Type = TrackType.Audio };
        a1.Clips.Add(new TimelineClipViewModel { Name = "background_music.mp3 (waveform placeholder)", Left = 0, Width = 600, ColorHex = "#3A784F" });
        Tracks.Add(a1);
    }
}
