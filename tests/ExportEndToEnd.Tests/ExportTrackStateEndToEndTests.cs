using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Timeline.Commands;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 15 Step 15.3 (D030 §4): the track flags set by the edit service's command (<see cref="SetTrackStateCommand"/>)
/// reach the export and the Preview through the shared snapshot — a hidden video track draws nothing (its sound still
/// plays), a muted track sounds nothing, a locked track plays and exports as before, and none of them changes the
/// length. The Preview drawn from the export's snapshot equals the export canvas byte for byte; no new criterion (the
/// canvas parity rule of D023).
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportTrackStateEndToEndTests
{
    private const string Red = "C83232";
    private const string Green = "20C040";
    private const int Rate = AudioFormat.SampleRate;
    private readonly E2EMedia _media;

    public ExportTrackStateEndToEndTests(E2EMedia media) => _media = media;

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    private static void Set(Track track, TrackStateFlag flag, bool value) => new SetTrackStateCommand(track, flag, value).Execute();

    private static float[] Slice(float[] x, double fromSeconds, double toSeconds) =>
        x[(int)(fromSeconds * Rate)..(int)Math.Min(x.Length, toSeconds * Rate)];

    private static async Task AssertPreviewEqualsExport(ExportRun run, params long[] frames)
    {
        foreach (var n in frames)
        {
            var preview = await Preview(run.Job.Snapshot, n);
            var canvas = run.Canvases[(int)n];
            Assert.Equal(canvas.Length, preview.Pixels.Length);
            var differing = Enumerable.Range(0, canvas.Length).Count(i => preview.Pixels[i] != canvas[i]);
            Assert.True(differing == 0, $"frame {n}: {differing} bytes differ between the Preview and the export canvas");
        }
    }

    private static (byte R, byte G, byte B) Centre(ExportRun run, int frame) =>
        Pixel(run.Canvases[frame], E2EMedia.Width, E2EMedia.Width / 2, E2EMedia.Height / 2);

    [FfmpegFact]
    public async Task A_hidden_video_track_is_absent_from_the_picture_a_locked_one_is_drawn_and_the_length_stays()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var v2 = p.VideoTrack();
        p.Video(v1, _media.Solid(Red, FrameRate.Fps25), 0, 10);
        p.Video(v2, _media.Solid(Green, FrameRate.Fps25), 0, 20);                  // V2 on top, and longer than V1

        var shown = await Export(p, "track-state-shown");
        AssertValidMp4(shown);
        Assert.True(Centre(shown, 0) is var (r0, g0, _) && g0 > r0, "V2 (green) is on top while shown");

        Set(v2, TrackStateFlag.Hidden, true);
        Set(v1, TrackStateFlag.Locked, true);
        var hidden = await Export(p, "track-state-hidden");

        AssertValidMp4(hidden);
        Assert.Equal(shown.Canvases.Count, hidden.Canvases.Count);                  // the length is unchanged: 20 frames
        Assert.Equal(20, hidden.Canvases.Count);
        var (r, g, b) = Centre(hidden, 0);
        Assert.True(r > g && r > 100, $"the locked V1 (red) shows where the hidden V2 was: R {r}, G {g}");
        var (r15, g15, b15) = Centre(hidden, 15);
        Assert.True(r15 < 10 && g15 < 10 && b15 < 10, $"after V1 ends nothing is drawn: {r15}, {g15}, {b15}");
        Assert.DoesNotContain(hidden.Job.Snapshot.VideoLayers, l => l.TrackId == v2.Id);
        await AssertPreviewEqualsExport(hidden, 0, 9, 15);

        Set(v2, TrackStateFlag.Hidden, false);                                      // shown again: as before
        var again = await Export(p, "track-state-shown-again");
        Assert.True(Centre(again, 0) is var (r2, g2, _) && g2 > r2);
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task A_muted_track_is_absent_from_the_sound_a_locked_one_is_heard_and_the_length_stays()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var a1 = p.AudioTrack();
        p.Video(v1, _media.Sync(FrameRate.Fps25), 0, 100);                         // bursts at 0.6 s, 1.8 s, 3.0 s
        p.Audio(a1, _media.Tone(), 0, 50);                                          // a tone over 0 … 2 s

        var both = await Export(p, "track-state-both");
        Assert.InRange(Rms(Slice(Left(both.Pcm), 0.1, 0.5)), 0.17, 0.18);           // the tone alone before the first burst

        Set(a1, TrackStateFlag.Muted, true);
        Set(v1, TrackStateFlag.Locked, true);
        var audioMuted = await Export(p, "track-state-a1-muted");
        var pcm = Left(audioMuted.Pcm);
        Assert.Equal(both.Pcm.Length, audioMuted.Pcm.Length);                       // the length is unchanged
        Assert.All(Slice(pcm, 0.1, 0.5), v => Assert.Equal(0f, v));                 // no tone
        Assert.True(Rms(Slice(pcm, 0.6, 0.64)) > 0.3, "the locked video track's burst is heard");
        Assert.DoesNotContain(audioMuted.Job.Snapshot.AudioSpans, s => a1.Clips.Any(c => c.Id == s.ClipId));

        Set(v1, TrackStateFlag.Muted, true);                                        // both muted: silence, same length
        var allMuted = await Export(p, "track-state-all-muted");
        AssertValidMp4(allMuted);
        Assert.Equal(both.Pcm.Length, allMuted.Pcm.Length);
        Assert.All(allMuted.Pcm, v => Assert.Equal(0f, v));
        Assert.Empty(allMuted.Job.Snapshot.AudioSpans);
        Assert.Equal(both.Canvases.Count, allMuted.Canvases.Count);
        await AssertPreviewEqualsExport(allMuted, 15, 45);                          // the picture unchanged by mute
        Assert.Equal(both.Canvases[15], allMuted.Canvases[15]);
        AssertNoFfmpegLeft();
    }
}
