using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Video.Tests;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 17 Step 17.6 (D032 R9, R10): an exported crossfade with the real decoder. The reference is each clip exported
/// alone, extended over the zone by an ordinary trim (no crossfade): the crossfade's samples are those two streams
/// weighted by <c>1 − ramp</c> and <c>ramp</c> and summed in the mix's order of operations — so the extension plays the
/// very source samples a trim would, and the ramps are the envelope's. The picture is not touched.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportCrossfadeEndToEndTests
{
    private readonly E2EMedia _media;

    public ExportCrossfadeEndToEndTests(E2EMedia media) => _media = media;

    private Task<ExportRun> Export(ProjectBuilder p, string name) =>
        ExportProject(p.Project, Path.Combine(_media.OutputFolder(), name + ".mp4"));

    /// <summary>V1: a black picture [0, 100); A1: A = tone [0, 50) from its frame 10, B = the recording's sound [50, 100) from
    /// frame 30 — with a crossfade of <paramref name="frames"/>, or (reference) A alone / B alone extended over the zone.</summary>
    private ProjectBuilder Scene(long frames, string which)
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        p.Video(p.VideoTrack(), _media.Solid("000000", FrameRate.Fps25), 0, 100);
        var a1 = p.AudioTrack();
        var (before, after) = TransitionRules.Zone(frames);
        if (which is "both" or "a")
            p.Audio(a1, _media.Tone(), 0, which == "a" ? 50 + after : 50, sourceInFrame: 10);
        if (which is "both" or "b")
            p.Audio(a1, _media.PatternWithTone(), which == "b" ? 50 - before : 50, 100, sourceInFrame: which == "b" ? 30 - before : 30);
        if (which == "both")
        {
            var (a, b) = (a1.Clips[0], a1.Clips[1]);
            a1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.Crossfade, Duration = p.F(frames), LeftClipId = a.Id, RightClipId = b.Id });
        }
        return p;
    }

    [FfmpegTheory]
    [InlineData(10)]
    [InlineData(25)]
    public async Task A_crossfade_is_the_two_extended_sounds_weighted_by_the_linear_ramps(long frames)
    {
        var crossfade = await Export(Scene(frames, "both"), $"crossfade-{frames}");
        var aloneA = await Export(Scene(frames, "a"), $"crossfade-{frames}-a");
        var aloneB = await Export(Scene(frames, "b"), $"crossfade-{frames}-b");
        AssertValidMp4(crossfade);

        var snapshot = crossfade.Job.Snapshot;
        var (spanA, spanB) = (snapshot.AudioSpans[0], snapshot.AudioSpans[1]);
        var (envA, envB) = (AudioFadeEnvelope.Of(spanA, snapshot.FrameRate), AudioFadeEnvelope.Of(spanB, snapshot.FrameRate));
        var (z0, z1) = (envB.CrossfadeInStart, envB.CrossfadeInEnd);
        Assert.Equal((z0, z1), (envA.CrossfadeOutStart, envA.CrossfadeOutEnd));
        Assert.True(z1 - z0 >= 1920 * 10);

        Assert.Equal(aloneA.Pcm.Length, crossfade.Pcm.Length);
        var differing = 0;
        for (long k = 0; k < crossfade.Pcm.Length / 2; k++)
        {
            for (var c = 0; c < 2; c++)
            {
                var i = (int)(2 * k + c);
                var mix = 0f;
                if (k < z1) mix += aloneA.Pcm[i] * (float)envA.Gain(k);   // A's alone run had no fade: its sample × 1
                if (k >= z0) mix += aloneB.Pcm[i] * (float)envB.Gain(k);
                mix = Math.Clamp(mix, -1f, 1f);
                if (mix != crossfade.Pcm[i]) differing++;
            }
        }
        Assert.True(differing == 0, $"{differing} samples differ from the weighted sum of the two extended sounds");
        Assert.Contains(crossfade.Pcm[(int)(2 * (z0 + z1) / 2)..(int)(2 * (z0 + z1) / 2 + 200)], s => s != 0);

        for (var n = 0; n < crossfade.Canvases.Count; n++)
            Assert.True(crossfade.Canvases[n].AsSpan().SequenceEqual(aloneA.Canvases[n]), $"frame {n}: the picture changed");
        AssertNoFfmpegLeft();
    }
}
