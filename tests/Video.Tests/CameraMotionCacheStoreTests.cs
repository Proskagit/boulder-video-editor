using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Motion;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// R2 → E, D4: the camera-motion tracks kept between sessions. A stored track comes back exactly; another key, a damaged,
/// truncated or foreign file is never read as a track (a damaged one is deleted, so the clip is analysed again); the
/// fingerprint changes with the file's content, size or write time and survives a move (relink); the folder is kept under
/// its size by removing the least recently used files.
/// </summary>
public sealed class CameraMotionCacheStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-cmt-" + Guid.NewGuid().ToString("N"));
    private readonly CameraMotionCacheStore _store;

    public CameraMotionCacheStoreTests() => _store = new CameraMotionCacheStore(_folder, NullLogger<CameraMotionCacheStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static CameraMotionTrack Track(int samples = 50) => new(MediaTime.FromSeconds(1), CameraMotionModel.Homography,
        Enumerable.Range(0, samples).Select(i => new CameraMotionSample(MediaTime.FromSeconds(1 + i / 30.0),
            new WallTransform(1 + i * 1e-4, 0.001, 0.01 * i, -0.002, 1, -0.005 * i, 1e-6, 2e-6, 1), i % 7 != 3)).ToImmutableArray(),
        MediaTime.FromSeconds(0.25), MediaTime.FromSeconds(0.1));

    private static void AssertSame(CameraMotionTrack expected, CameraMotionTrack actual)
    {
        Assert.Equal(expected.ReferenceTime, actual.ReferenceTime);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.MaxInterpolationGap, actual.MaxInterpolationGap);
        Assert.Equal(expected.EdgeTolerance, actual.EdgeTolerance);
        Assert.Equal(expected.Samples.ToArray(), actual.Samples.ToArray());   // bit-exact doubles
    }

    [Fact]
    public void A_stored_track_comes_back_exactly_under_its_key_only()
    {
        var track = Track();
        _store.Save("key-a", track);
        AssertSame(track, _store.TryLoad("key-a")!);
        Assert.Null(_store.TryLoad("key-b"));
        Assert.Single(Directory.GetFiles(_folder, "*" + CameraMotionCacheStore.Extension));
    }

    [Fact]
    public void A_damaged_or_truncated_file_is_not_a_track_and_is_removed()
    {
        _store.Save("key", Track());
        var file = Directory.GetFiles(_folder).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(file, bytes);
        Assert.Null(_store.TryLoad("key"));
        Assert.False(File.Exists(file));                                   // analysed again next time

        _store.Save("key", Track());
        File.WriteAllBytes(file, File.ReadAllBytes(file)[..100]);
        Assert.Null(_store.TryLoad("key"));
        File.WriteAllText(file, "not a track");
        Assert.Null(_store.TryLoad("key"));
    }

    [Fact]
    public void A_file_written_for_another_key_is_never_read_as_this_one()
    {
        _store.Save("other", Track());
        var other = Directory.GetFiles(_folder).Single();
        _store.Save("this", Track(3));
        var mine = Directory.GetFiles(_folder).Single(f => f != other);
        File.Copy(other, mine, overwrite: true);                           // a file under the wrong name (a hash collision)
        Assert.Null(_store.TryLoad("this"));
    }

    [Fact]
    public void The_fingerprint_follows_the_content_and_survives_a_move()
    {
        Directory.CreateDirectory(_folder);
        var video = Path.Combine(_folder, "clip.mp4");
        var content = new byte[3 * 1024 * 1024 + 17];
        new Random(5).NextBytes(content);
        File.WriteAllBytes(video, content);
        var time = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(video, time);
        var first = _store.Fingerprint(video);
        Assert.NotNull(first);
        Assert.Equal(first, _store.Fingerprint(video));

        var moved = Path.Combine(_folder, "relinked.mp4");                 // relinked to the same file elsewhere
        File.Move(video, moved);
        File.SetLastWriteTimeUtc(moved, time);
        Assert.Equal(first, _store.Fingerprint(moved));

        content[10] ^= 1;                                                  // another video of the same size and time
        File.WriteAllBytes(moved, content);
        File.SetLastWriteTimeUtc(moved, time);
        Assert.NotEqual(first, _store.Fingerprint(moved));

        File.SetLastWriteTimeUtc(moved, time.AddSeconds(1));               // the same bytes, touched
        Assert.NotEqual(_store.Fingerprint(video), _store.Fingerprint(moved));
        Assert.Null(_store.Fingerprint(Path.Combine(_folder, "missing.mp4")));
    }

    [Fact]
    public void An_unwritable_folder_is_logged_and_nothing_is_thrown()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder should be");
        try
        {
            _store.Save("key", Track());                                   // no exception
            Assert.Null(_store.TryLoad("key"));
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    [Fact]
    public void Above_its_limit_the_least_recently_used_tracks_are_removed()
    {
        var one = CameraMotionCacheStore.Encode(Track(), "k").Length;
        var store = new CameraMotionCacheStore(_folder, NullLogger<CameraMotionCacheStore>.Instance, maxBytes: 3 * one);
        var time = DateTime.UtcNow.AddMinutes(-10);
        foreach (var key in new[] { "a", "b", "c" })
        {
            store.Save(key, Track());
            foreach (var f in Directory.GetFiles(_folder)) if (File.GetLastWriteTimeUtc(f) > time) File.SetLastWriteTimeUtc(f, time);
            time = time.AddMinutes(1);
        }
        Assert.NotNull(store.TryLoad("a"));                                // used: now the most recent
        store.Save("d", Track());
        Assert.Equal(3, Directory.GetFiles(_folder, "*" + CameraMotionCacheStore.Extension).Length);
        Assert.Null(store.TryLoad("b"));                                   // the least recently used went
        Assert.NotNull(store.TryLoad("a"));
        Assert.NotNull(store.TryLoad("d"));
    }

    [Fact]
    public void A_track_round_trips_through_its_bytes_and_any_flipped_byte_is_refused()
    {
        var track = Track(5);
        var bytes = CameraMotionCacheStore.Encode(track, "k");
        AssertSame(track, CameraMotionCacheStore.Decode(bytes, "k")!);
        for (var i = 0; i < bytes.Length; i += 7)
        {
            var damaged = (byte[])bytes.Clone();
            damaged[i] ^= 0x01;
            Assert.Null(CameraMotionCacheStore.Decode(damaged, "k"));
        }
    }
}
