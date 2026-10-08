using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Phase 15 (D029 §3 → D030 §9, Q15): "Playing without sound…" is reported once when playback runs without an audio
/// output; when the sound is available again the stale message goes (only if it is still the one shown), and a later
/// loss is reported again. A status only — nothing in the project.
/// </summary>
public sealed class PreviewSoundStatusTests
{
    private const string NoSound = "Playing without sound: no audio output is available.";

    private readonly StatusService _status = new();
    private readonly StubPlayback _playback = new();
    private readonly ProjectService _projects = new(new UndoRedoService(), NullLogger<ProjectService>.Instance);

    private PreviewViewModel Preview() => new(_status, _playback, _projects, NullLogger<PreviewViewModel>.Instance);

    [Fact]
    public void The_message_goes_when_the_sound_is_back_and_a_later_loss_is_reported_again()
    {
        var preview = Preview();
        _playback.Playing = true;
        _playback.Audio = false;

        preview.Tick();
        Assert.Equal(NoSound, _status.Message);

        _playback.Audio = true;                                                       // the device works again
        preview.Tick();
        Assert.Equal("Ready.", _status.Message);
        Assert.False(_projects.Current.IsDirty);

        _playback.Audio = false;                                                      // lost again: said again
        preview.Tick();
        Assert.Equal(NoSound, _status.Message);
    }

    [Fact]
    public void A_newer_message_is_not_cleared_when_the_sound_comes_back()
    {
        var preview = Preview();
        _playback.Playing = true;
        _playback.Audio = false;
        preview.Tick();
        _status.Report("Saved project \"x\".");

        _playback.Audio = true;
        preview.Tick();

        Assert.Equal("Saved project \"x\".", _status.Message);
    }

    [Fact]
    public void While_paused_the_message_stays_until_sound_plays_again()
    {
        var preview = Preview();
        _playback.Playing = true;
        _playback.Audio = false;
        preview.Tick();
        _playback.Playing = false;
        _playback.Audio = true;                                                       // available, but nothing plays yet
        preview.Tick();
        Assert.Equal(NoSound, _status.Message);

        preview.PlayCommand.Execute(null);                                            // Play again, with sound
        preview.Tick();
        Assert.Equal("Ready.", _status.Message);
    }

    private sealed class StubPlayback : IPlaybackService
    {
        public bool Playing { get; set; }
        public bool Audio { get; set; } = true;
        public PlaybackState State => Playing ? PlaybackState.Playing : PlaybackState.Paused;
        public bool IsBuffering => false;
        public bool IsAvailable => true;
        public bool IsAudioAvailable => Audio;
        public MediaTime Position => MediaTime.Zero;
        public MediaTime Duration => MediaTime.FromSeconds(10);
        public MediaTime PlaybackEnd => Duration;
        public event EventHandler? StateChanged { add { } remove { } }
        public void UpdateSnapshot(PlaybackSnapshot snapshot) { }
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Stop() => Playing = false;
        public void SetPlaybackRange(PlaybackRange? range) { }
        public Task<bool> SeekAsync(MediaTime position, CancellationToken ct = default) => Task.FromResult(true);
        public PlaybackFrame Update() => new(Position, 0, State, false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
