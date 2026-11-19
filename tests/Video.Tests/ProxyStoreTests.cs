using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>D071 §3: the proxies' cache — one per content and quality, made once, a changed file is another key, a failed
/// making stores nothing, the least recently used go first above the limit, «Удалить прокси» removes them.</summary>
public sealed class ProxyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aive-proxy-store", Guid.NewGuid().ToString("N"));

    public ProxyStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private MediaAsset Source(string name, int bytes = 4096, int width = 3840, int height = 2160, string codec = "h264")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Enumerable.Range(0, bytes).Select(i => (byte)(i * 7 + name[0])).ToArray());   // per name: other content
        return new MediaAsset
        {
            FilePath = path, Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Width = width, Height = height, DisplayWidth = width, DisplayHeight = height, VideoCodec = codec, PixelFormat = "yuv420p", Duration = MediaTime.FromSeconds(5) }
        };
    }

    private ProxyStore Store(FakeBuilder builder, long maxBytes = ProxyStore.DefaultMaxBytes) =>
        new(Path.Combine(_dir, "proxies"), builder, NullLogger<ProxyStore>.Instance, maxBytes);

    [Fact]
    public async Task One_1080p_proxy_is_made_once_per_content()
    {
        var builder = new FakeBuilder();
        var store = Store(builder);
        var asset = Source("a.mp4");

        Assert.Null(store.TryGet(asset));
        var path = await store.GetOrCreateAsync(asset, null, default);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(path, store.TryGet(asset));
        Assert.Equal(path, await store.GetOrCreateAsync(asset, null, default));
        Assert.Single(builder.Requests);
        Assert.Equal((1920, 1080), (builder.Requests[0].Size.Width, builder.Requests[0].Size.Height));
    }

    [Fact]
    public async Task The_same_file_in_two_projects_has_one_proxy_and_a_changed_file_another()
    {
        var builder = new FakeBuilder();
        var store = Store(builder);
        var asset = Source("a.mp4");
        var other = new MediaAsset { FilePath = asset.FilePath, Kind = asset.Kind, AnalysisStatus = asset.AnalysisStatus, Metadata = asset.Metadata };

        var path = await store.GetOrCreateAsync(asset, null, default);
        Assert.Equal(path, store.TryGet(other));

        File.WriteAllBytes(asset.FilePath, new byte[5000]);   // the file edited
        Assert.Null(store.TryGet(asset));
    }

    [Fact]
    public async Task A_file_found_HDR_later_does_not_take_its_SDR_proxy()
    {
        var builder = new FakeBuilder();
        var store = Store(builder);
        var asset = Source("a.mp4", codec: "hevc");
        var sdr = await store.GetOrCreateAsync(asset, null, default);
        Assert.Null(builder.Requests[0].Hdr);

        // D033 T3: the probe finds HLG — another picture, another proxy, made with the conversion.
        asset.Metadata!.PixelFormat = "yuv420p10le";
        asset.Metadata.BitDepth = 10;
        asset.Metadata.ColorTransfer = "arib-std-b67";
        asset.Metadata.ColorPrimaries = "bt2020";
        asset.Metadata.ColorMatrix = "bt2020nc";
        Assert.Null(store.TryGet(asset));
        var hdr = await store.GetOrCreateAsync(asset, null, default);
        Assert.NotEqual(sdr, hdr);
        Assert.NotNull(builder.Requests[1].Hdr);
    }

    [Fact]
    public async Task A_temporary_file_of_a_making_that_never_ended_is_removed_a_recent_one_kept()
    {
        var store = Store(new FakeBuilder());
        Directory.CreateDirectory(store.Folder);
        var stale = Path.Combine(store.Folder, "abc-medium.1234.tmp.mp4");
        var recent = Path.Combine(store.Folder, "def-medium.5678.tmp.mp4");
        File.WriteAllBytes(stale, new byte[10]);
        File.WriteAllBytes(recent, new byte[10]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - ProxyStore.StaleTemporaryAge - TimeSpan.FromMinutes(1));

        await store.GetOrCreateAsync(Source("a.mp4"), null, default);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task A_video_without_need_gets_no_proxy_and_a_failed_making_stores_nothing()
    {
        var builder = new FakeBuilder();
        var store = Store(builder);
        Assert.Null(await store.GetOrCreateAsync(Source("hd.mp4", width: 1920, height: 1080), null, default));
        Assert.Empty(builder.Requests);

        builder.Fail = true;
        var asset = Source("a.mp4");
        await Assert.ThrowsAsync<ProxyBuildException>(() => store.GetOrCreateAsync(asset, null, default));
        Assert.Null(store.TryGet(asset));
        Assert.DoesNotContain(Directory.GetFiles(store.Folder), f => !f.Contains(".tmp."));
    }

    [Fact]
    public async Task Above_the_limit_the_least_recently_used_proxies_go_first()
    {
        var builder = new FakeBuilder { Bytes = 1000 };
        var store = Store(builder, maxBytes: 2500);
        var a = Source("a.mp4");
        var b = Source("b.mp4");
        var c = Source("c.mp4");

        await store.GetOrCreateAsync(a, null, default);
        await store.GetOrCreateAsync(b, null, default);
        File.SetLastWriteTimeUtc(store.TryGet(b)!, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(store.TryGet(a)!, DateTime.UtcNow.AddHours(-1));   // a used after b
        await store.GetOrCreateAsync(c, null, default);

        Assert.NotNull(store.TryGet(a));
        Assert.Null(store.TryGet(b));
        Assert.NotNull(store.TryGet(c));
    }

    [Fact]
    public async Task Clearing_removes_every_proxy_and_reports_the_bytes()
    {
        var store = Store(new FakeBuilder { Bytes = 1500 });
        await store.GetOrCreateAsync(Source("a.mp4"), null, default);
        await store.GetOrCreateAsync(Source("b.mp4", codec: "hevc", width: 1920, height: 1080), null, default);
        Assert.Equal(3000, store.SizeBytes());   // a.mp4 4K and b.mp4 1080p HEVC

        Assert.Equal(3000, store.Clear());
        Assert.Equal(0, store.SizeBytes());
    }

    private sealed class FakeBuilder : IProxyBuilder
    {
        public List<ProxyBuildRequest> Requests { get; } = new();
        public bool Fail { get; set; }
        public int Bytes { get; set; } = 100;

        public Task BuildAsync(ProxyBuildRequest request, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Fail) throw new ProxyBuildException("fake failure");
            File.WriteAllBytes(request.OutputPath, new byte[Bytes]);
            return Task.CompletedTask;
        }
    }
}
