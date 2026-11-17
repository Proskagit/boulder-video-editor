using AiVideoEditor.Core.Audio;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D064 §4–§5: the clean-ups the clips use and the speech the ducked tracks listen to are computed in the background, once per
/// file and setting (both parts of a split share one); the results reach the snapshot; a missing model fails with its message;
/// the user cancels and retries; the export waits for what is pending and warns about what failed; nothing is an undo step.
/// </summary>
public sealed class AudioProcessingCoordinatorTests : IDisposable
{
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TestUiThread _ui = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-apc-" + Guid.NewGuid().ToString("N"));

    public AudioProcessingCoordinatorTests()
    {
        _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        _ui.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private AudioClip AddAudio(AudioCleanup? cleanup, Track? track = null, MediaAsset? asset = null)
    {
        if (asset is null)
        {
            var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, new byte[100]);
            asset = new MediaAsset
            {
                FilePath = path, Kind = MediaKind.Audio, AnalysisStatus = MediaAnalysisStatus.Completed,
                Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(2), AudioCodec = "pcm_f32le" }
            };
            _projects.AddMediaAssets(new[] { asset });
        }
        var clip = new AudioClip { MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(1), SourceOut = MediaTime.FromSeconds(1), Cleanup = cleanup };
        (track ?? _projects.Current.Timeline.AudioTracks[0]).Clips.Add(clip);
        return clip;
    }

    private sealed class Service : IAudioCleanupService
    {
        public string? Unavailable { get; set; }
        public int Produced { get; private set; }
        public int Analysed { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public List<string> SpeechPaths { get; } = new();
        public string Version => "test";

        public Task<string?> CheckAsync(AudioCleanup cleanup, CancellationToken ct) => Task.FromResult(cleanup.VoiceEnhancement > 0 ? Unavailable : null);
        public Task<string?> CheckSpeechAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public async Task ProduceAsync(string filePath, MediaTime startTime, MediaTime duration, AudioCleanup cleanup, string outputPath,
            IProgress<double>? progress, CancellationToken ct)
        {
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            Produced++;
            await File.WriteAllBytesAsync(outputPath, new byte[64], ct);
        }

        public Task<SpeechActivity> AnalyseSpeechAsync(string filePath, MediaTime startTime, MediaTime duration, IProgress<double>? progress, CancellationToken ct)
        {
            Analysed++;
            SpeechPaths.Add(filePath);
            return Task.FromResult(new SpeechActivity(new float[] { 0.9f, 0.9f }));
        }
    }

    private AudioProcessingCoordinator New(Service service) =>
        new(_projects, service) { StartDelay = TimeSpan.Zero };

    [Fact]
    public Task A_clean_up_is_computed_once_per_file_and_setting_and_reaches_the_snapshot() => _ui.Run(async () =>
    {
        var clip = AddAudio(new AudioCleanup(40, 0));
        AddAudio(new AudioCleanup(40, 0), asset: _projects.Current.MediaAssets[0]);      // the same file and setting (a split's part)
        var service = new Service();
        using var coordinator = New(service);
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();

        Assert.Equal(1, service.Produced);
        Assert.Equal(AudioJobState.Ready, coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup).State);
        var file = Assert.Single(coordinator.Analyses.Cleaned.Files).Value;
        Assert.True(File.Exists(file));
        var snapshot = Core.Playback.PlaybackSnapshotBuilder.Build(_projects.Current, 1, audioAnalyses: coordinator.Analyses);
        Assert.All(snapshot.AudioSpans, s => Assert.Equal(file, s.CleanedSource!.FilePath));
        Assert.Empty(coordinator.ForExport().PendingCleanups);
        Assert.False(_undo.CanUndo);
    });

    [Fact]
    public Task Without_the_voice_model_the_clean_up_fails_with_its_message_and_the_export_warns() => _ui.Run(async () =>
    {
        var clip = AddAudio(new AudioCleanup(0, 60));
        var service = new Service { Unavailable = "no model here" };
        using var coordinator = New(service);
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();

        var status = coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup);
        Assert.Equal(AudioJobState.Failed, status.State);
        Assert.Equal("no model here", status.Message);
        Assert.Equal("no model here", coordinator.ForExport().FailedCleanups[(clip.MediaAssetId, clip.Cleanup!.Value)]);

        service.Unavailable = null;                                                       // installed meanwhile
        coordinator.Retry();
        await coordinator.IdleAsync();
        Assert.Equal(AudioJobState.Ready, coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup).State);
    });

    [Fact]
    public Task A_running_clean_up_blocks_the_export_and_a_cancel_stops_it_until_retried() => _ui.Run(async () =>
    {
        var clip = AddAudio(new AudioCleanup(50, 0));
        var service = new Service { Gate = new TaskCompletionSource() };
        using var coordinator = New(service);
        _projects.NotifyTimelineChanged();
        await Task.Delay(50);
        Assert.Equal(AudioJobState.Processing, coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup).State);
        Assert.Contains((clip.MediaAssetId, clip.Cleanup!.Value), coordinator.ForExport().PendingCleanups);

        coordinator.Cancel(clip.MediaAssetId, clip.Cleanup);
        await coordinator.IdleAsync();
        Assert.Equal(AudioJobState.Cancelled, coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup).State);
        Assert.Contains((clip.MediaAssetId, clip.Cleanup!.Value), coordinator.ForExport().PendingCleanups);   // still not exported without it

        service.Gate = null;
        coordinator.Retry();
        await coordinator.IdleAsync();
        Assert.Equal(AudioJobState.Ready, coordinator.CleanupStatus(clip.MediaAssetId, clip.Cleanup).State);
    });

    [Fact]
    public Task The_ducking_analyses_the_speech_tracks_cleaned_sound_after_its_clean_up() => _ui.Run(async () =>
    {
        var timeline = _projects.Current.Timeline;
        var speech = new Track { Type = TrackType.Audio, Name = "A2", Order = 1 };
        timeline.AudioTracks.Add(speech);
        var voice = AddAudio(new AudioCleanup(0, 80), speech);
        AddAudio(null);                                                                   // the music on A1
        timeline.AudioTracks[0].Ducking = DuckingSettings.Default(speech.Id);
        var service = new Service();
        using var coordinator = New(service);
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();

        Assert.Equal(1, service.Produced);
        Assert.Equal(1, service.Analysed);
        Assert.Equal(coordinator.Analyses.Cleaned.Files.Single().Value, Assert.Single(service.SpeechPaths));
        Assert.Equal(AudioJobState.Ready, coordinator.DuckingStatus(timeline.AudioTracks[0].Id).State);
        Assert.True(coordinator.Analyses.Speech.ByAsset.ContainsKey((voice.MediaAssetId, voice.Cleanup!.Value)));
        Assert.Empty(coordinator.ForExport().PendingDucking);
    });

    [Fact]
    public Task The_comparison_plays_the_original_sound_and_is_never_exported() => _ui.Run(async () =>
    {
        AddAudio(new AudioCleanup(40, 0));
        using var coordinator = New(new Service());
        _projects.NotifyTimelineChanged();
        await coordinator.IdleAsync();
        var changed = 0;
        coordinator.AnalysesChanged += (_, _) => changed++;
        coordinator.OriginalSound = true;
        Assert.Equal(1, changed);
        Assert.Null(Core.Playback.PlaybackSnapshotBuilder.Build(_projects.Current, 1, audioAnalyses: coordinator.Analyses).AudioSpans.Single().CleanedSource);
        Assert.False(coordinator.ForExport().Analyses.OriginalSound);
    });
}
