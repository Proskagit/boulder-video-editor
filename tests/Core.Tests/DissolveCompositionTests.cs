using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Composition;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>
/// Phase 10 Step 10.7 (D025 §3, PO-5, PO-6, PO-8): the snapshot of a dissolve — its zone on the frame grid, A and B
/// extended into it, both layers of the track in the zone (A below, B at its opacity × (j+1)/(F+1)), occlusion, the
/// prefetch edges, a changed dissolve never presentation-only, the sound untouched, fades of the cut's edges off.
/// </summary>
public class DissolveCompositionTests
{
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private readonly Project _project = new() { Settings = { FrameRate = Rate } };   // 1920 × 1080
    private readonly Track _v1 = new() { Type = TrackType.Video, Name = "V1", Order = 0 };
    private readonly Track _v2 = new() { Type = TrackType.Video, Name = "V2", Order = 1 };

    public DissolveCompositionTests() => _project.Timeline.VideoTracks.AddRange(new[] { _v1, _v2 });

    private VideoClip Video(Track track, long start, long end, long sourceIn = 0, double opacity = 1, double scale = 1)
    {
        var asset = new MediaAsset
        {
            FilePath = $@"C:\media\{Guid.NewGuid():N}.mp4", Kind = MediaKind.Video,
            Metadata = new MediaMetadata
            {
                Duration = MediaTime.FromSeconds(60), FrameRate = Rate, AudioCodec = "aac",
                Width = 1920, Height = 1080, DisplayRotation = 0, DisplayWidth = 1920, DisplayHeight = 1080
            }
        };
        _project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = F(start), Duration = F(end) - F(start), SourceIn = F(sourceIn),
            Opacity = opacity, Scale = scale
        };
        clip.SourceOut = clip.SourceIn + clip.Duration;
        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.TimelineStart.CompareTo(b.TimelineStart));
        return clip;
    }

    private static Transition Dissolve(Track track, Clip a, Clip b, long frames)
    {
        var t = new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = F(frames), LeftClipId = a.Id, RightClipId = b.Id };
        track.Transitions.Add(t);
        return t;
    }

    private PlaybackSnapshot Snapshot() => PlaybackSnapshotBuilder.Build(_project, 1);
    private VideoLayer V1Layer(PlaybackSnapshot s) => s.VideoLayers.Single(l => l.TrackId == _v1.Id);

    [Fact]
    public void The_zone_lies_on_the_frame_grid_around_the_cut_and_extends_both_clips()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, sourceIn: 60);
        Dissolve(_v1, a, b, 7);                                                   // 3 frames before the cut, 4 after

        var layer = V1Layer(Snapshot());

        var zone = Assert.Single(layer.Dissolves);
        Assert.Equal((a.Id, b.Id, F(47), F(50), F(54), 7L), (zone.LeftClipId, zone.RightClipId, zone.Start, zone.Cut, zone.End, zone.Frames));
        Assert.Equal((F(0), F(54)), (layer.Spans[0].ShownStart, layer.Spans[0].ShownEnd));
        Assert.Equal((F(47), F(100)), (layer.Spans[1].ShownStart, layer.Spans[1].ShownEnd));
        Assert.Equal((F(50), F(60)), (layer.Spans[1].TimelineStart, layer.Spans[1].SourceIn));   // the timing anchor is unchanged
    }

    [Fact]
    public void In_the_zone_A_is_below_B_and_B_ramps_in_frame_by_frame()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100, opacity: 0.8);
        Dissolve(_v1, a, b, 10);                                                  // zone [45, 55)
        var snapshot = Snapshot();

        Assert.Equal(a.Id, Assert.Single(snapshot.LayersAt(F(44))).ClipId);
        for (long j = 0; j < 10; j++)
        {
            var layers = snapshot.LayersAt(F(45 + j));
            Assert.Equal(new[] { a.Id, b.Id }, layers.Select(l => l.ClipId));
            Assert.Equal(1.0, layers[0].Opacity);
            Assert.Equal(0.8 * ((j + 1) / 11.0), layers[1].Opacity);
            Assert.False(((PictureLayer)layers[1]).OccludesBelow);
        }
        var after = Assert.Single(snapshot.LayersAt(F(55)));
        Assert.Equal((b.Id, 0.8), (after.ClipId, after.Opacity));
    }

    [Fact]
    public void An_opaque_A_hides_the_tracks_below_in_the_zone_and_a_transparent_one_doesnt()
    {
        var bottom = Video(_v1, 0, 100);
        var a = Video(_v2, 0, 50);
        var b = Video(_v2, 50, 100);
        Dissolve(_v2, a, b, 10);

        Assert.Equal(new[] { a.Id, b.Id }, Snapshot().LayersAt(F(48)).Select(l => l.ClipId));   // V1 culled under A

        a.Opacity = 0.5;
        Assert.Equal(new[] { bottom.Id, a.Id, b.Id }, Snapshot().LayersAt(F(48)).Select(l => l.ClipId));
    }

    [Fact]
    public void A_dissolve_on_the_lower_track_shows_under_a_partly_covering_upper_clip()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        Dissolve(_v1, a, b, 10);
        var top = Video(_v2, 0, 100, scale: 0.5);                                // doesn't cover the canvas

        Assert.Equal(new[] { a.Id, b.Id, top.Id }, Snapshot().LayersAt(F(50)).Select(l => l.ClipId));
    }

    [Fact]
    public void Text_and_images_take_part_like_videos()
    {
        var a = Video(_v1, 0, 50);
        var text = new TextClip { Text = "Title", TimelineStart = F(50), Duration = F(50), Opacity = 0.5 };
        _v1.Clips.Add(text);
        Dissolve(_v1, a, text, 4);

        var layers = Snapshot().LayersAt(F(49));

        Assert.Equal(new[] { a.Id, text.Id }, layers.Select(l => l.ClipId));
        Assert.IsType<TextLayer>(layers[1]);
        Assert.Equal(0.5 * (2 / 5.0), layers[1].Opacity);                         // zone [48, 52): j = 1
    }

    [Fact]
    public void Fades_on_the_cuts_edges_are_off_and_the_free_edges_still_fade()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        (a.FadeIn, a.FadeOut, b.FadeIn, b.FadeOut) = (F(4), F(10), F(10), F(5));
        Dissolve(_v1, a, b, 10);
        var snapshot = Snapshot();

        Assert.Equal(1.0 / 5, snapshot.LayersAt(F(0))[0].Opacity);                // A's fade in (a free edge)
        var zone = snapshot.LayersAt(F(47));
        Assert.Equal(1.0, zone[0].Opacity);                                        // A's fade out is off (PO-8)
        Assert.Equal(3 / 11.0, zone[1].Opacity);                                   // only the dissolve, no fade in of B
        Assert.Equal(1.0 / 6, snapshot.LayersAt(F(99))[0].Opacity);               // B's fade out (a free edge)
    }

    [Fact]
    public void The_zone_edges_are_picture_changes_and_a_dissolve_change_reopens_decoders()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        var without = Snapshot();
        var dissolve = Dissolve(_v1, a, b, 10);
        var with = Snapshot();

        Assert.Equal(F(45), with.NextPictureChange(F(0)));
        Assert.Equal(F(50), with.NextPictureChange(F(45)));
        Assert.Equal(F(55), with.NextPictureChange(F(50)));
        Assert.False(without.DiffersOnlyInPresentation(with));

        dissolve.Duration = F(12);
        Assert.False(with.DiffersOnlyInPresentation(Snapshot()));
    }

    [Fact]
    public void The_sound_is_a_hard_cut()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        var without = Snapshot().AudioSpans;
        Dissolve(_v1, a, b, 10);

        Assert.Equal(without.ToArray(), Snapshot().AudioSpans.ToArray());
    }

    [Fact]
    public void A_hidden_track_has_no_zones()
    {
        var a = Video(_v1, 0, 50);
        var b = Video(_v1, 50, 100);
        Dissolve(_v1, a, b, 10);
        _v1.IsHidden = true;

        Assert.DoesNotContain(Snapshot().VideoLayers, l => l.TrackId == _v1.Id);
    }
}
