using System.Collections.Immutable;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.People;
using AiVideoEditor.Media.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Video.Tests;

/// <summary>
/// D061 §5: the people analyses kept between sessions. Tracks and masks come back exactly; another key, a damaged or
/// truncated file is never read (a damaged one is deleted, so the video is analysed again); the folder stays under its size
/// limit by removing the least recently used files; the key's fingerprint changes with the file.
/// </summary>
public sealed class PeopleAnalysisCacheStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aive-ppl-" + Guid.NewGuid().ToString("N"));
    private readonly PeopleAnalysisCacheStore _store;

    public PeopleAnalysisCacheStoreTests() => _store = new PeopleAnalysisCacheStore(_folder, NullLogger<PeopleAnalysisCacheStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static PersonTracks Tracks()
    {
        var samples = Enumerable.Range(0, 40).Select(i => new PersonSample(MediaTime.FromSeconds(i * 0.1),
            ImmutableArray.Create(
                new PersonDetection(new NormalizedBox(0.1 + i * 0.01, 0.2, 0.3 + i * 0.01, 0.8), 0.91f, new NormalizedPoint(0.2 + i * 0.01, 0.45),
                    ImmutableArray.Create(Enumerable.Range(0, PersonAppearance.Length).Select(k => k / 100f).ToArray()))
                {
                    Keypoints = ImmutableArray.Create(Enumerable.Range(0, PersonDetection.KeypointCount * 3).Select(k => (k + i) / 60f).ToArray())
                },
                new PersonDetection(new NormalizedBox(0.6, 0.3, 0.8, 0.9), 0.4f, null, ImmutableArray<float>.Empty)),
            ImmutableArray.Create(0, i % 3 == 0 ? -1 : 1))).ToImmutableArray();
        return PersonTracks.Of(MediaTime.FromSeconds(0.1), samples, "people-v1");
    }

    private static PersonMaskTrack Masks()
    {
        var grid = new byte[36 * 64];
        for (var y = 10; y < 40; y++) for (var x = 5; x < 20; x++) grid[y * 36 + x] = (byte)(x * 10);
        var samples = Enumerable.Range(0, 20).Select(i => new PersonMaskSample(MediaTime.FromSeconds(i / 15.0),
            ImmutableArray.Create(MaskCodec.Entry(7, grid, 36, 64)!))).ToImmutableArray();
        return new PersonMaskTrack(36, 64, PeopleAnalyzer.MaskInterval, samples, "people-v1");
    }

    [Fact]
    public void Tracks_come_back_exactly()
    {
        var tracks = Tracks();
        _store.SaveTracks("k1", tracks);
        var loaded = _store.TryLoadTracks("k1")!;
        Assert.Equal(tracks.ModelId, loaded.ModelId);
        Assert.Equal(tracks.Interval, loaded.Interval);
        Assert.Equal(tracks.Tracks.ToArray(), loaded.Tracks.ToArray());
        for (var i = 0; i < tracks.Samples.Length; i++)
        {
            Assert.Equal(tracks.Samples[i].Time, loaded.Samples[i].Time);
            Assert.Equal(tracks.Samples[i].TrackIds.ToArray(), loaded.Samples[i].TrackIds.ToArray());
            for (var p = 0; p < tracks.Samples[i].People.Length; p++)
            {
                var (a, b) = (tracks.Samples[i].People[p], loaded.Samples[i].People[p]);
                Assert.Equal((a.Box, a.Score, a.Torso), (b.Box, b.Score, b.Torso));
                Assert.Equal(a.Appearance.ToArray(), b.Appearance.ToArray());
                Assert.Equal(a.Keypoints.ToArray(), b.Keypoints.ToArray());   // D078 §3
            }
        }
    }

    [Fact]
    public void Masks_come_back_exactly()
    {
        var masks = Masks();
        _store.SaveMasks("k1", masks);
        var loaded = _store.TryLoadMasks("k1")!;
        Assert.Equal((masks.Width, masks.Height, masks.Interval, masks.ModelId), (loaded.Width, loaded.Height, loaded.Interval, loaded.ModelId));
        Assert.Equal(masks.CoverageAt(MediaTime.FromSeconds(0.5))!.Coverage, loaded.CoverageAt(MediaTime.FromSeconds(0.5))!.Coverage);
        Assert.Equal(new[] { 7 }, loaded.PersonIds);
    }

    [Fact]
    public void Another_key_is_not_found_and_tracks_are_not_masks()
    {
        _store.SaveTracks("k1", Tracks());
        Assert.Null(_store.TryLoadTracks("k2"));
        Assert.Null(_store.TryLoadMasks("k1"));
    }

    [Fact]
    public void A_damaged_file_is_never_read_and_is_deleted()
    {
        _store.SaveMasks("k1", Masks());
        var file = Directory.GetFiles(_folder, "*" + PeopleAnalysisCacheStore.MasksExtension).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(file, bytes);
        Assert.Null(_store.TryLoadMasks("k1"));
        Assert.False(File.Exists(file));

        _store.SaveTracks("k2", Tracks());
        var tracksFile = Directory.GetFiles(_folder, "*" + PeopleAnalysisCacheStore.TracksExtension).Single();
        File.WriteAllBytes(tracksFile, File.ReadAllBytes(tracksFile)[..100]);   // truncated
        Assert.Null(_store.TryLoadTracks("k2"));
    }

    [Fact]
    public void The_folder_stays_under_its_limit()
    {
        var small = new PeopleAnalysisCacheStore(_folder, NullLogger<PeopleAnalysisCacheStore>.Instance, maxBytes: 30_000);
        for (var i = 0; i < 10; i++) small.SaveTracks($"k{i}", Tracks());
        var total = Directory.GetFiles(_folder).Sum(f => new FileInfo(f).Length);
        Assert.True(total <= 30_000, $"{total} bytes");
        Assert.NotNull(small.TryLoadTracks("k9"));   // the newest is kept
    }

    [Fact]
    public void The_file_just_saved_is_kept_even_when_every_write_time_is_the_same()
    {
        // Files written within the clock's resolution share a write time (and a later one may even look older): the one just
        // saved is never the one pruned.
        _store.SaveTracks("probe", Tracks());
        var size = Directory.GetFiles(_folder).Sum(f => new FileInfo(f).Length);
        var small = new PeopleAnalysisCacheStore(_folder, NullLogger<PeopleAnalysisCacheStore>.Instance, maxBytes: size * 2 + size / 2);
        for (var i = 0; i < 4; i++) small.SaveTracks($"old{i}", Tracks());
        var future = DateTime.UtcNow.AddMinutes(5);
        foreach (var f in Directory.GetFiles(_folder)) File.SetLastWriteTimeUtc(f, future);
        small.SaveTracks("new", Tracks());
        Assert.NotNull(small.TryLoadTracks("new"));
        Assert.True(Directory.GetFiles(_folder).Sum(f => new FileInfo(f).Length) <= size * 2 + size / 2);
    }

    [Fact]
    public void The_fingerprint_changes_with_the_file()
    {
        Directory.CreateDirectory(_folder);
        var video = Path.Combine(_folder, "clip.mp4");
        File.WriteAllBytes(video, Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var first = _store.Fingerprint(video);
        Assert.NotNull(first);
        File.WriteAllBytes(video, Enumerable.Range(0, 5000).Select(i => (byte)(i + 1)).ToArray());
        Assert.NotEqual(first, _store.Fingerprint(video));
        Assert.Null(_store.Fingerprint(Path.Combine(_folder, "missing.mp4")));
    }
}
