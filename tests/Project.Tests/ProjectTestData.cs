using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Project.Tests;

/// <summary>A project that exercises every stored field, on an NTSC grid so tick values
/// are "uneven" (frame boundaries rounded to the nearest tick).</summary>
internal static class ProjectTestData
{
    public static readonly FrameRate Rate = FrameRate.Ntsc30;

    public static MediaTime Frame(long n) => MediaTime.FromFrame(n, Rate);

    public static Core.Entities.Project Build(string mediaFolder)
    {
        var project = new Core.Entities.Project
        {
            Name = "Round Trip",
            CreatedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 9, 23, 12, 34, 56, 789, TimeSpan.FromHours(3)),
            Settings = new ProjectSettings
            {
                FrameWidth = 3840,
                FrameHeight = 2160,
                FrameRate = Rate,
                IsFrameRateLocked = true,
                AudioSampleRate = 44100
            },
            LastExportSettings = new ExportSettings
            {
                OutputPath = Path.Combine(mediaFolder, "out.mp4")
            }
        };

        var video = new MediaAsset
        {
            FilePath = Path.Combine(mediaFolder, "clip.mp4"),
            FileSizeBytes = 123_456_789,
            Kind = MediaKind.Video,
            ThumbnailPath = "thumbnails/clip.png",
            ImportedAt = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero),
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = new MediaTime(1_234_567_891),
                Width = 3840,
                Height = 2160,
                FrameRate = FrameRate.Ntsc30,
                AvgFrameRate = new FrameRate(2997, 100),
                StartTime = new MediaTime(14_000),
                VideoCodec = "h264",
                AudioCodec = "aac",
                AudioChannels = 2,
                AudioSampleRate = 48000,
                BitrateBps = 25_000_000
            }
        };
        var audio = new MediaAsset
        {
            FilePath = Path.Combine(mediaFolder, "music.wav"),
            FileSizeBytes = 42,
            Kind = MediaKind.Audio,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = new MediaTime(9_000_000_001), AudioCodec = "pcm_s16le", AudioChannels = 1, AudioSampleRate = 44100 }
        };
        var image = new MediaAsset
        {
            FilePath = Path.Combine(mediaFolder, "still.png"),
            Kind = MediaKind.Image,
            AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.Zero, Width = 640, Height = 480, VideoCodec = "png" }
        };
        project.MediaAssets.AddRange(new[] { video, audio, image });

        var seq = project.Timeline;
        seq.Name = "Main";
        seq.PlayheadPosition = new MediaTime(123_456_789);
        seq.ZoomPixelsPerSecond = 87.25;
        seq.SnappingEnabled = false;
        seq.Markers.Add(new Marker { Position = Frame(90), Label = "Beat", ColorHex = "#FF0000" });

        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1, IsHidden = true, IsLocked = true };
        var a1 = new Track { Type = TrackType.Audio, Name = "A1", Order = 0, IsMuted = true };

        var videoClip = new VideoClip
        {
            MediaAssetId = video.Id,
            TimelineStart = Frame(1),
            Duration = Frame(1001) - Frame(1),
            SourceIn = new MediaTime(3_336_667),
            PositionX = 0.125, PositionY = -3.5, Scale = 1.5, RotationDegrees = 12.75, Opacity = 0.8, Volume = 0.3333333333333333,
            Crop = new CropRect(0.1, 0.2, 0.3, 0.05)
        };
        videoClip.SourceOut = videoClip.SourceIn + videoClip.Duration;
        videoClip.Effects.Add(new Effect
        {
            EffectTypeId = "blur",
            DisplayName = "Blur",
            IsEnabled = false,
            Parameters = { ["radius"] = 2.5, ["passes"] = 3L, ["mode"] = "gauss", ["enabled"] = true, ["extra"] = null }
        });

        var imageClip = new ImageClip
        {
            MediaAssetId = image.Id,
            TimelineStart = Frame(1001),
            Duration = Frame(1151) - Frame(1001),
            SourceIn = MediaTime.Zero,
            Opacity = 0.5,
            Crop = CropRect.None
        };
        imageClip.SourceOut = imageClip.Duration;

        var textClip = new TextClip
        {
            TimelineStart = Frame(30),
            Duration = Frame(60) - Frame(30),
            Text = "Hello, \"world\" — ünïcode",
            FontFamily = "Arial",
            FontSize = 36.5,
            ColorHex = "#00FF00",
            Alignment = TextAlignment.Right,
            PositionY = 100
        };

        var audioClip = new AudioClip
        {
            MediaAssetId = audio.Id,
            TimelineStart = Frame(0),
            Duration = Frame(2000),
            SourceIn = new MediaTime(7),
            Volume = 0.25,
            IsMuted = true
        };
        audioClip.SourceOut = audioClip.SourceIn + audioClip.Duration;

        v1.Clips.AddRange(new Clip[] { videoClip, imageClip });
        v1.Transitions.Add(new Transition { TransitionTypeId = "fade", Duration = Frame(10) });
        v2.Clips.Add(textClip);
        a1.Clips.Add(audioClip);

        seq.VideoTracks.AddRange(new[] { v1, v2 });
        seq.AudioTracks.Add(a1);
        return project;
    }
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiVideoEditorTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string CreateFile(string relativePath, string content = "x")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
