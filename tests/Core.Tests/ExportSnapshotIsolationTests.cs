using System.Collections.Immutable;
using System.Text.Json;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Export;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// D060 §2: the export's job is an independent copy of the project. After the job is made, editing the project — moving
/// and trimming clips, changing text, opacity, transforms, routes, templates, tracks, the canvas, the frame rate, the export
/// settings, relinking or removing media — changes nothing the job exports: neither its snapshot (compared value by value,
/// serialized in full) nor the composition it draws on any frame.
/// </summary>
public class ExportSnapshotIsolationTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private const string Output = @"C:\Exports\out.mp4";

    /// <summary>A stand-in decoded frame (the composition's geometry, not its pixels, is compared).</summary>
    private static readonly DecodedFrame Frame = new(16, 9, 64, new byte[64 * 9], new SourceTimestamp(0, new TimeBase(1, 25)));

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1", Order = 0 };
    private readonly MediaAsset _video;
    private readonly VideoClip _clip;
    private readonly TextClip _title;
    private readonly TemplateClip _boulder;

    public ExportSnapshotIsolationTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _project.Timeline.VideoTracks.Add(_v2);
        _project.Timeline.AudioTracks.Add(_a1);
        _video = new MediaAsset
        {
            FilePath = @"C:\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), Width = 1920, Height = 1080, DisplayWidth = 1920, DisplayHeight = 1080,
                FrameRate = Rate, StartTime = MediaTime.Zero, AudioCodec = "aac"
            }
        };
        _project.MediaAssets.Add(_video);
        _clip = Add(_v1, new VideoClip { MediaAssetId = _video.Id, Opacity = 0.8, Scale = 1.1, PositionX = 20 }, 0, 200);
        _clip.Routes = ImmutableArray.Create(new HoldMarkup
        {
            StrokeWidth1080 = 12, ColorHex = "#FFEA00",
            Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), ImmutableArray.Create(
                new NormalizedPoint(0.1, 0.1), new NormalizedPoint(0.3, 0.1), new NormalizedPoint(0.3, 0.3)), default, default, MediaTime.Zero))
        });
        _title = Add(_v2, new TextClip { Text = "Трасса 7", FontSize = 40 }, 10, 120);
        _boulder = Add(_v2, new TemplateClip { Kind = TemplateKind.Boulder, Route = "7" }, 130, 190);
        Add(_a1, new AudioClip { MediaAssetId = _video.Id, Volume = 0.7 }, 0, 200);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private static T Add<T>(Track track, T clip, long startFrame, long endFrame) where T : Clip
    {
        clip.TimelineStart = F(startFrame);
        clip.Duration = F(endFrame) - F(startFrame);
        if (clip is MediaBackedClip media) media.SourceOut = media.SourceIn + clip.Duration;
        track.Clips.Add(clip);
        return clip;
    }

    private ExportJob Job()
    {
        var result = ExportPreflight.Check(_project, Output, new ExportPreflightEnvironment(true, _ => true, _ => true, path => path == @"C:\Exports"));
        Assert.True(result.CanExport, string.Join("; ", result.Issues.Select(i => i.Message)));
        return result.Job!;
    }

    /// <summary>Everything the job exports, as text: the snapshot in full, the output, and the composition of every frame.</summary>
    private static string Fingerprint(ExportJob job)
    {
        var snapshot = job.Snapshot;
        var frames = Enumerable.Range(0, (int)job.Output.FrameCount).Select(n =>
        {
            var layers = snapshot.LayersAt(MediaTime.FromFrame(n, snapshot.FrameRate));
            var plan = CompositionDrawPlan.Build(snapshot.Canvas, Affine2D.Identity, layers.Select(l => new ResolvedLayer(l, l is PictureLayer ? Frame : null)));
            return string.Join("|", plan.Operations.Select(o => $"{o.GetType().Name}:{o.ClipId}:{o.Transform}:{o.Opacity}"));
        });
        return string.Join("\n", new[]
        {
            JsonSerializer.Serialize(snapshot.VideoLayers, Json),
            JsonSerializer.Serialize(snapshot.AudioSpans, Json),
            JsonSerializer.Serialize(snapshot.Assets, Json),
            $"{snapshot.Canvas} {snapshot.FrameRate} {snapshot.Duration}",
            JsonSerializer.Serialize(job.Output, Json),
            JsonSerializer.Serialize(job.Encoding, Json)
        }.Concat(frames));
    }

    [Fact]
    public void Editing_the_project_after_the_job_was_made_changes_nothing_it_exports()
    {
        var job = Job();
        var before = Fingerprint(job);
        Assert.Contains("Трасса 7", before);

        // Everything the user can change while the export runs (D060 §2).
        _clip.TimelineStart = F(20);
        _clip.Opacity = 0.1;
        _clip.Scale = 2;
        _clip.RotationDegrees = 45;
        _clip.SourceIn = F(30);
        _clip.Routes = ImmutableArray<HoldMarkup>.Empty;
        _title.Text = "Другой текст";
        _title.FontSize = 90;
        _title.Opacity = 0.2;
        _boulder.Route = "99";
        _boulder.Kind = TemplateKind.Vhs;
        _v2.IsHidden = true;
        _a1.IsMuted = true;
        _project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V3", Order = 2 });
        _project.Settings.FrameWidth = 1080;
        _project.Settings.FrameHeight = 1920;
        _project.Settings.FrameRate = FrameRate.Fps60;
        _project.Settings.Export = ExportEncoding.Default with { Resolution = ExportResolution.Sd, Encoder = ExportVideoEncoder.Nvenc };
        _video.FilePath = @"D:\relinked\other.mp4";       // relink
        _video.IsMissing = true;
        _project.MediaAssets.Clear();                      // removed from the project
        _v1.Clips.Clear();

        Assert.Equal(before, Fingerprint(job));
        Assert.Equal(@"C:\media\climb.mp4", Assert.Single(job.Snapshot.Assets.Values).FilePath);
    }

    [Fact]
    public void Replacing_the_project_by_another_does_not_reach_the_job()
    {
        var job = Job();
        var before = Fingerprint(job);
        // The project object itself is changed beyond recognition (as a project opened in its place would be).
        _project.Timeline.VideoTracks.Clear();
        _project.Timeline.AudioTracks.Clear();
        _project.Name = "Другой проект";
        Assert.Equal(before, Fingerprint(job));
    }

    [Fact]
    public void A_new_job_after_the_edits_sees_them()
    {
        var first = Fingerprint(Job());
        _title.Text = "Новый заголовок";
        var second = Fingerprint(Job());
        Assert.NotEqual(first, second);
        Assert.Contains("Новый заголовок", second);
    }
}
