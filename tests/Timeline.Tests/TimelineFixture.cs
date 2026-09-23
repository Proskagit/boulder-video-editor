using System.Text;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Project;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>Real ProjectService + UndoRedoService + TimelineEditService, plus helpers
/// to create analyzed media and to place clips directly for re-grid scenarios.</summary>
internal sealed class TimelineFixture
{
    public ProjectService Projects { get; } = new(NullLogger<ProjectService>.Instance);
    public UndoRedoService UndoRedo { get; } = new();
    public TimelineEditService Service { get; }
    public int TimelineChangedCount { get; private set; }

    public TimelineFixture()
    {
        Service = new TimelineEditService(Projects, UndoRedo, NullLogger<TimelineEditService>.Instance);
        Projects.TimelineChanged += (_, _) => TimelineChangedCount++;
    }

    public Core.Entities.Project Project => Projects.Current;
    public ProjectSettings Settings => Project.Settings;
    public Track V1 => Project.Timeline.VideoTracks[0];
    public Track A1 => Project.Timeline.AudioTracks[0];
    public FrameRate Rate => Settings.FrameRate;

    public MediaAsset Video(double seconds, FrameRate? rate, string name = "clip.mp4") =>
        AddAsset(name, MediaKind.Video, new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), FrameRate = rate, Width = 1920, Height = 1080 });

    public MediaAsset Audio(double seconds, string name = "music.mp3") =>
        AddAsset(name, MediaKind.Audio, new MediaMetadata { Duration = MediaTime.FromSeconds(seconds), AudioSampleRate = 48000 });

    public MediaAsset Image(string name = "logo.png") => AddAsset(name, MediaKind.Image, null, MediaAnalysisStatus.Pending);

    public MediaAsset AddAsset(string name, MediaKind kind, MediaMetadata? metadata, MediaAnalysisStatus status = MediaAnalysisStatus.Completed)
    {
        var asset = new MediaAsset
        {
            FilePath = Path.Combine(Path.GetTempPath(), "ai-video-editor-tests", Guid.NewGuid().ToString("N"), name),
            Kind = kind,
            Metadata = metadata,
            AnalysisStatus = status
        };
        Project.MediaAssets.Add(asset);
        return asset;
    }

    /// <summary>Places a clip directly into the model (bypassing the service) at exact
    /// tick positions, for setting up re-grid scenarios.</summary>
    public T Place<T>(Track track, MediaAsset asset, long startTicks, long endTicks, long sourceInTicks = 0) where T : MediaBackedClip
    {
        MediaBackedClip created = typeof(T) == typeof(AudioClip) ? new AudioClip { MediaAssetId = asset.Id }
            : typeof(T) == typeof(VideoClip) ? new VideoClip { MediaAssetId = asset.Id }
            : new ImageClip { MediaAssetId = asset.Id };
        var clip = (T)created;
        new ClipState(new MediaTime(startTicks), new MediaTime(endTicks - startTicks), new MediaTime(sourceInTicks),
            new MediaTime(sourceInTicks + endTicks - startTicks)).ApplyTo(clip);
        var index = track.Clips.FindIndex(c => c.TimelineStart.Ticks > startTicks);
        track.Clips.Insert(index < 0 ? track.Clips.Count : index, clip);
        return clip;
    }

    public ImageClip PlaceImageFrames(Track track, MediaAsset image, long startFrame, long endFrame, FrameRate rate) =>
        Place<ImageClip>(track, image, MediaTime.FromFrame(startFrame, rate).Ticks, MediaTime.FromFrame(endFrame, rate).Ticks);

    public MediaAsset? FindAsset(Guid id) => Project.MediaAssets.FirstOrDefault(a => a.Id == id);

    public void AssertValid()
    {
        var error = TimelineValidator.ValidateSequence(Project.Timeline, Rate, FindAsset);
        Assert.True(error is null, error);
    }

    /// <summary>Exact textual snapshot of everything timeline commands may change.</summary>
    public string Snapshot()
    {
        var sb = new StringBuilder();
        sb.Append($"rate={Settings.FrameRate};locked={Settings.IsFrameRateLocked}\n");
        foreach (var track in Project.Timeline.VideoTracks.Concat(Project.Timeline.AudioTracks))
        {
            sb.Append($"{track.Id}:{track.Name}|");
            foreach (var clip in track.Clips)
            {
                var s = ClipState.Capture(clip);
                sb.Append($"{clip.Id}:{clip.GetType().Name}:{s.Start.Ticks},{s.Duration.Ticks},{s.SourceIn.Ticks},{s.SourceOut.Ticks};");
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static IReadOnlyList<Guid> Ids(params Clip[] clips) => clips.Select(c => c.Id).ToList();
}
