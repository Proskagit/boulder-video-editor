using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

public class PlaybackSnapshotBuilderTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };
    private readonly Track _a1 = new() { Type = TrackType.Audio, Name = "A1", Order = 0 };

    public PlaybackSnapshotBuilderTests()
    {
        _project.Timeline.VideoTracks.Add(_v1);
        _project.Timeline.VideoTracks.Add(_v2);
        _project.Timeline.AudioTracks.Add(_a1);
    }

    private static MediaTime F(long frame) => MediaTime.FromFrame(frame, Rate);

    private MediaAsset Asset(MediaKind kind, bool withAudio = true, bool analyzed = true)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.{(kind == MediaKind.Image ? "png" : "mp4")}",
            Kind = kind,
            Metadata = analyzed && kind != MediaKind.Image
                ? new MediaMetadata
                {
                    Duration = MediaTime.FromSeconds(60), FrameRate = Rate, AvgFrameRate = Rate,
                    StartTime = MediaTime.FromSeconds(1.4), AudioCodec = withAudio ? "aac" : null
                }
                : null
        };
        _project.MediaAssets.Add(asset);
        return asset;
    }

    private static T Add<T>(Track track, T clip, long startFrame, long endFrame, long sourceInFrame = 0) where T : Clip
    {
        clip.TimelineStart = F(startFrame);
        clip.Duration = F(endFrame) - F(startFrame);
        if (clip is MediaBackedClip media)
        {
            media.SourceIn = F(sourceInFrame);
            media.SourceOut = media.SourceIn + clip.Duration;
        }
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private PlaybackSnapshot Build(long version = 1) => PlaybackSnapshotBuilder.Build(_project, version);

    [Fact]
    public void TopmostVisibleTrackWins_LowerClipIsNotSplit()
    {
        var a = Asset(MediaKind.Video);
        var b = Asset(MediaKind.Video);
        var low = Add(_v1, new VideoClip { MediaAssetId = a.Id }, 0, 100, sourceInFrame: 10);
        var high = Add(_v2, new VideoClip { MediaAssetId = b.Id }, 40, 60);

        var snapshot = Build();

        Assert.Equal(low.Id, snapshot.PictureAt(F(39))!.ClipId);
        Assert.Equal(high.Id, snapshot.PictureAt(F(40))!.ClipId);
        Assert.Equal(high.Id, snapshot.PictureAt(F(59))!.ClipId);
        Assert.Equal(low.Id, snapshot.PictureAt(F(60))!.ClipId);

        // The lower clip is still one logical span with its own start and SourceIn.
        var lowSpan = Assert.Single(snapshot.VideoLayers.Single(l => l.TrackId == _v1.Id).Spans);
        Assert.Equal((F(0), F(100), F(10)), (lowSpan.TimelineStart, lowSpan.TimelineEnd, lowSpan.SourceIn));
        Assert.Equal(_v2.Id, snapshot.VideoLayers[0].TrackId); // topmost first
    }

    [Fact]
    public void HiddenTrack_HasNoPicture_ButKeepsItsAudio()
    {
        var a = Asset(MediaKind.Video);
        var b = Asset(MediaKind.Video);
        var low = Add(_v1, new VideoClip { MediaAssetId = a.Id }, 0, 100);
        var hidden = Add(_v2, new VideoClip { MediaAssetId = b.Id, Volume = 0.5 }, 0, 50);
        _v2.IsHidden = true;

        var snapshot = Build();

        Assert.Equal(low.Id, snapshot.PictureAt(F(10))!.ClipId);
        Assert.DoesNotContain(snapshot.VideoLayers, l => l.TrackId == _v2.Id);
        var audio = Assert.Single(snapshot.AudioSpans, s => s.ClipId == hidden.Id);
        Assert.Equal(0.5, audio.Gain);
        Assert.Equal(SpanStatus.Audio, audio.Status);
    }

    [Fact]
    public void Gaps_And_NextPictureChange()
    {
        var a = Asset(MediaKind.Video);
        Add(_v1, new VideoClip { MediaAssetId = a.Id }, 10, 20);
        Add(_v1, new VideoClip { MediaAssetId = a.Id }, 30, 40);

        var snapshot = Build();

        Assert.Null(snapshot.PictureAt(F(0)));
        Assert.Null(snapshot.PictureAt(F(20)));
        Assert.Equal(F(10), snapshot.NextPictureChange(F(0)));
        Assert.Equal(F(20), snapshot.NextPictureChange(F(10)));
        Assert.Equal(F(30), snapshot.NextPictureChange(F(20)));
        Assert.Equal(snapshot.Duration, snapshot.NextPictureChange(F(40)));
        Assert.Equal(F(40), snapshot.Duration);
    }

    [Fact]
    public void Muting_And_Volume_AndVideoWithoutAudio()
    {
        var withAudio = Asset(MediaKind.Video);
        var silent = Asset(MediaKind.Video, withAudio: false);
        var music = Asset(MediaKind.Audio);
        var v = Add(_v1, new VideoClip { MediaAssetId = withAudio.Id, Volume = 0.8 }, 0, 10);
        var s = Add(_v1, new VideoClip { MediaAssetId = silent.Id }, 10, 20);
        var a = Add(_a1, new AudioClip { MediaAssetId = music.Id, Volume = 0.3 }, 0, 20);
        var muted = Add(_a1, new AudioClip { MediaAssetId = music.Id, IsMuted = true }, 20, 30);

        var snapshot = Build();
        Assert.Equal(0.8, snapshot.AudioSpans.Single(x => x.ClipId == v.Id).Gain);
        Assert.Equal(0.3, snapshot.AudioSpans.Single(x => x.ClipId == a.Id).Gain);
        Assert.DoesNotContain(snapshot.AudioSpans, x => x.ClipId == s.Id);
        Assert.DoesNotContain(snapshot.AudioSpans, x => x.ClipId == muted.Id);

        _a1.IsMuted = true;
        _v1.IsMuted = true;
        Assert.Empty(Build(2).AudioSpans);
        Assert.NotNull(Build(3).PictureAt(F(5))); // muting never hides the picture
    }

    [Fact]
    public void Offline_Unsupported_StillImage_AndTransparentText()
    {
        var missing = Asset(MediaKind.Video);
        missing.IsMissing = true;
        var unanalyzed = Asset(MediaKind.Video, analyzed: false);
        var normal = Asset(MediaKind.Video);
        var image = Asset(MediaKind.Image);

        var cMissing = Add(_v1, new VideoClip { MediaAssetId = missing.Id }, 0, 10);
        var cUnknown = Add(_v1, new VideoClip { MediaAssetId = Guid.NewGuid() }, 10, 20);
        var cUnanalyzed = Add(_v1, new VideoClip { MediaAssetId = unanalyzed.Id }, 20, 30);
        var cSpeed = Add(_v1, new VideoClip { MediaAssetId = normal.Id, Speed = 2.0 }, 30, 40);
        var cWrongKind = Add(_v1, new VideoClip { MediaAssetId = image.Id }, 40, 50);
        var cImage = Add(_v1, new ImageClip { MediaAssetId = image.Id }, 50, 60);
        Add(_v2, new TextClip { Text = "title" }, 0, 60);

        var snapshot = Build();

        Assert.Equal(SpanStatus.Offline, snapshot.PictureAt(F(0))!.Status);
        Assert.Equal(cMissing.Id, snapshot.PictureAt(F(0))!.ClipId); // text above is transparent
        Assert.Equal((SpanStatus.Offline, cUnknown.Id), (snapshot.PictureAt(F(10))!.Status, snapshot.PictureAt(F(10))!.ClipId));
        Assert.Equal((SpanStatus.Offline, cUnanalyzed.Id), (snapshot.PictureAt(F(20))!.Status, snapshot.PictureAt(F(20))!.ClipId));
        Assert.Equal((SpanStatus.Unsupported, cSpeed.Id), (snapshot.PictureAt(F(30))!.Status, snapshot.PictureAt(F(30))!.ClipId));
        Assert.Equal((SpanStatus.Unsupported, cWrongKind.Id), (snapshot.PictureAt(F(40))!.Status, snapshot.PictureAt(F(40))!.ClipId));
        Assert.Equal((SpanStatus.StillImage, cImage.Id), (snapshot.PictureAt(F(50))!.Status, snapshot.PictureAt(F(50))!.ClipId));
        Assert.All(new[] { 0, 10, 20, 30, 40 }, f => Assert.False(string.IsNullOrEmpty(snapshot.PictureAt(F(f))!.Reason)));
        Assert.Null(snapshot.PictureAt(F(50))!.Reason);
    }

    [Fact]
    public void AssetsCarryDecodingInputs_AndSnapshotIsIndependentOfLaterEdits()
    {
        var a = Asset(MediaKind.Video);
        var clip = Add(_v1, new VideoClip { MediaAssetId = a.Id }, 0, 10);

        var snapshot = Build(version: 41);
        clip.TimelineStart = F(100);
        a.FilePath = @"C:\elsewhere.mp4";
        _v1.Clips.Clear();

        Assert.Equal(41, snapshot.SnapshotVersion);
        Assert.Equal(Rate, snapshot.FrameRate);
        Assert.Equal(F(10), snapshot.Duration);
        Assert.Equal(F(0), snapshot.PictureAt(F(0))!.TimelineStart);
        var asset = snapshot.Assets[a.Id];
        Assert.NotEqual(@"C:\elsewhere.mp4", asset.FilePath);
        Assert.Equal((MediaTime.FromSeconds(1.4), (FrameRate?)Rate), (asset.StartTime, asset.NominalFrameRate));
    }

    [Fact]
    public void SnapshotTypeGraph_HoldsNoMutableProjectEntities()
    {
        // The background pipeline only sees these types; none may reference Project, Sequence,
        // Track, Clip or MediaAsset (enums such as MediaKind are values and allowed).
        var entityTypes = typeof(Project).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(Project).Namespace && !t.IsEnum && !t.IsValueType)
            .ToHashSet();
        var visited = new HashSet<Type>();
        var pending = new Queue<Type>(new[] { typeof(PlaybackSnapshot) });
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!visited.Add(type)) continue;
            Assert.DoesNotContain(type, entityTypes);
            if (type.Namespace?.StartsWith("AiVideoEditor") != true && !type.IsGenericType) continue;

            var members = type.IsGenericType ? type.GetGenericArguments()
                : type.GetProperties().Select(p => p.PropertyType)
                    .Concat(type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(f => f.FieldType));
            foreach (var member in members)
                pending.Enqueue(Nullable.GetUnderlyingType(member) ?? member);
        }
        Assert.Contains(typeof(PictureSpan), visited);
        Assert.Contains(typeof(AudioSpan), visited);
        Assert.Contains(typeof(PlaybackAsset), visited);
    }
}
