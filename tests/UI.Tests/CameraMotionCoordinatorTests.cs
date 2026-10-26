using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// Hold tracking (R2 → E): the coordinator analyses a clip with holds from its video (a fake decoder showing a wall that
/// pans), publishes a track that follows the pan, keeps the clip pending until then (the export waits), leaves clips
/// without holds alone, never adds an Undo step, and a failing decoder leaves the contours
/// untracked instead of blocking anything.
/// </summary>
public sealed class CameraMotionCoordinatorTests : IDisposable
{
    private const int W = 480, H = 270;
    private const double PanPixelsPerSecond = 12;
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _projects;
    private readonly TestUiThread _ui = new();
    public void Dispose() => _ui.Dispose();

    public CameraMotionCoordinatorTests() => _projects = new ProjectService(_undo, NullLogger<ProjectService>.Instance);

    private VideoClip AddClip(bool withHolds)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(2), Width = W, Height = H, DisplayWidth = W, DisplayHeight = H,
                FrameRate = FrameRate.Fps25, StartTime = MediaTime.Zero
            }
        };
        _projects.AddMediaAssets(new[] { asset });
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, Duration = MediaTime.FromSeconds(2),
            HoldMarkup = withHolds
                ? RouteColor.WithResolved(new HoldMarkup
                {
                    Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(),
                        ImmutableArray.Create(new NormalizedPoint(0.4, 0.4), new NormalizedPoint(0.6, 0.4), new NormalizedPoint(0.5, 0.6)),
                        RouteColor.HexToOkLab("#1E88E5"), RouteColor.HexToOkLab("#EEEEEE"), MediaTime.Zero))
                })
                : null
        };
        clip.SourceOut = clip.Duration;
        _projects.Current.Timeline.VideoTracks[0].Clips.Add(clip);
        return clip;
    }

    [Fact]
    public Task A_clip_with_holds_gets_a_track_that_follows_the_pan() => _ui.Run(async () =>
    {
        var clip = AddClip(withHolds: true);
        var plain = AddClip(withHolds: false);
        using var coordinator = new CameraMotionCoordinator(_projects, new PanningDecoder());
        Assert.Contains(clip.Id, coordinator.Pending);           // pending until analysed: the export waits
        Assert.DoesNotContain(plain.Id, coordinator.Pending);

        await coordinator.IdleAsync();

        Assert.Empty(coordinator.Pending);
        var track = coordinator.Tracks[clip.Id];
        Assert.False(coordinator.Tracks.ContainsKey(plain.Id));
        var at = track.At(MediaTime.FromSeconds(1));
        Assert.Equal(CameraMotionLookupKind.Aligned, at.Kind);
        var (x, y) = at.Transform.Apply(0.5, 0.5);
        Assert.Equal(0.5 + PanPixelsPerSecond / W, x, 3);     // the wall moved right by 12 px in one second
        Assert.Equal(0.5, y, 3);
        Assert.Same(track, coordinator.ForExport().Tracks[clip.Id]);
        Assert.False(_undo.CanUndo);
    });

    [Fact]
    public Task A_failing_decoder_leaves_the_contours_untracked() => _ui.Run(async () =>
    {
        var clip = AddClip(withHolds: true);
        using var coordinator = new CameraMotionCoordinator(_projects, new FailingDecoder());
        await coordinator.IdleAsync();
        Assert.Empty(coordinator.Pending);
        Assert.False(coordinator.Tracks.ContainsKey(clip.Id));
    });

    [Fact]
    public Task Removing_the_holds_removes_the_track() => _ui.Run(async () =>
    {
        var clip = AddClip(withHolds: true);
        using var coordinator = new CameraMotionCoordinator(_projects, new PanningDecoder());
        await coordinator.IdleAsync();
        var changes = 0;
        coordinator.TracksChanged += (_, _) => changes++;
        clip.HoldMarkup = null;
        _projects.NotifyTimelineChanged();
        Assert.Empty(coordinator.Tracks);
        Assert.Equal(1, changes);
    });

    /// <summary>A synthetic wall that pans right at <see cref="PanPixelsPerSecond"/>, 10 frames per second, 2 s.</summary>
    internal sealed class PanningDecoder : IVideoDecoder
    {
        private static readonly float[] Wall = SyntheticWall();

        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default)
        {
            var first = (int)Math.Floor((double)(request.FirstSamplePoint.TicksNumerator / request.FirstSamplePoint.TicksDenominator) / TimeSpan.TicksPerSecond * 10);
            return Task.FromResult<IVideoFrameStream>(new Stream(Math.Max(0, first)));
        }

        private sealed class Stream(int next) : IVideoFrameStream
        {
            public ValueTask<DecodedFrame?> ReadFrameAsync(CancellationToken ct = default)
            {
                if (next >= 20) return ValueTask.FromResult<DecodedFrame?>(null);
                var shift = PanPixelsPerSecond * next / 10.0;
                var pixels = new byte[W * H * 4];
                for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var v = (byte)Math.Clamp(Sample(x - shift, y), 0, 255);
                    var o = (y * W + x) * 4;
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = v;
                    pixels[o + 3] = 255;
                }
                var frame = new DecodedFrame(W, H, W * 4, pixels, new SourceTimestamp(next, new TimeBase(1, 10)));
                next++;
                return ValueTask.FromResult<DecodedFrame?>(frame);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private static float Sample(double x, double y)
        {
            x = Math.Clamp(x, 0, W - 1.001);
            int x0 = (int)x;
            var f = (float)(x - x0);
            var i = (int)y * W + x0;
            return Wall[i] + (Wall[i + 1] - Wall[i]) * f;
        }

        private static float[] SyntheticWall()
        {
            var random = new Random(5);
            var pixels = new float[W * H];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = 200 + (float)random.NextDouble() * 5;
            for (var k = 0; k < 80; k++)
            {
                double cx = random.NextDouble() * W, cy = random.NextDouble() * H, r = 4 + random.NextDouble() * 10;
                var value = (float)(30 + random.NextDouble() * 120);
                for (var y = Math.Max(0, (int)(cy - r)); y < Math.Min(H, (int)(cy + r) + 1); y++)
                for (var x = Math.Max(0, (int)(cx - r)); x < Math.Min(W, (int)(cx + r) + 1); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) pixels[y * W + x] = value + (float)(x - cx);
            }
            return pixels;
        }
    }

    internal sealed class FailingDecoder : IVideoDecoder
    {
        public Task<IVideoFrameStream> OpenAsync(VideoDecodeRequest request, CancellationToken ct = default) =>
            throw new VideoDecodeException(VideoDecodeError.DecoderUnavailable, "no decoder");
    }
}
