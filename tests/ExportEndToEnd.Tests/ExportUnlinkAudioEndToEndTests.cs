using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Timeline;
using AiVideoEditor.Video.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AiVideoEditor.ExportEndToEnd.Tests.EndToEnd;

namespace AiVideoEditor.ExportEndToEnd.Tests;

/// <summary>
/// Phase 17 Step 17.4 (D032 R3, AQ8, AQ11): Unlink Audio never changes what is heard or seen — the export after it is
/// byte for byte the export before it (every canvas and every mixed sample), where no other sound overlaps the clip: at
/// 1× with volume and fades, at another speed, with a dissolve on the video track whose edge holds a fade that the sound
/// never had, and on a muted video track. No new criterion: the audio clip plays the video file's sound through the same
/// placement, fade and mix rules.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportUnlinkAudioEndToEndTests
{
    private readonly E2EMedia _media;

    public ExportUnlinkAudioEndToEndTests(E2EMedia media) => _media = media;

    private static TimelineEditService Edit(ProjectBuilder p) =>
        new(new SceneProject(p.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

    private async Task AssertUnlinkChangesNothing(ProjectBuilder p, string name, params VideoClip[] clips)
    {
        var before = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), $"{name}-before.mp4"));
        var edit = Edit(p);
        foreach (var clip in clips)
        {
            var result = edit.UnlinkAudio(clip.Id);
            Assert.True(result.Success, result.Message);
        }
        Assert.All(clips, c => Assert.True(c.IsAudioDetached));
        var videoFiles = clips.Select(c => c.MediaAssetId).ToHashSet();
        Assert.Equal(clips.Length, p.Project.Timeline.AudioTracks.SelectMany(t => t.Clips).OfType<AudioClip>()
            .Count(a => videoFiles.Contains(a.MediaAssetId)));

        var after = await ExportProject(p.Project, Path.Combine(_media.OutputFolder(), $"{name}-after.mp4"));
        AssertValidMp4(after);
        Assert.Equal(before.Canvases.Count, after.Canvases.Count);
        for (var i = 0; i < before.Canvases.Count; i++)
            Assert.True(before.Canvases[i].AsSpan().SequenceEqual(after.Canvases[i]), $"{name}: frame {i} differs");
        Assert.Equal(before.Pcm.Length, after.Pcm.Length);
        Assert.True(before.Pcm.AsSpan().SequenceEqual(after.Pcm), $"{name}: the sound differs after Unlink Audio");
        Assert.Contains(before.Pcm, s => s != 0);
        AssertNoFfmpegLeft();
    }

    [FfmpegFact]
    public async Task Unlink_audio_at_1x_with_volume_and_fades_exports_byte_for_byte_as_before()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        p.AudioTrack();
        var clip = p.Video(v1, _media.PatternWithTone(), 5, 70, sourceInFrame: 12);
        clip.Volume = 0.7;
        (clip.FadeIn, clip.FadeOut) = (p.F(6), p.F(9));

        await AssertUnlinkChangesNothing(p, "unlink-1x", clip);
    }

    [FfmpegFact]
    public async Task Unlink_audio_at_another_speed_exports_byte_for_byte_as_before()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var clip = p.Video(v1, _media.PatternWithTone(), 0, 40, sourceInFrame: 3, speed: ClipSpeed.FromSteps(30));   // 1.5×

        await AssertUnlinkChangesNothing(p, "unlink-speed", clip);   // no audio track: Unlink Audio adds one
    }

    [FfmpegFact]
    public async Task Unlink_audio_with_a_dissolve_and_a_fade_on_its_edge_exports_byte_for_byte_as_before()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        p.AudioTrack();
        var tone = _media.PatternWithTone();
        var a = p.Video(v1, tone, 0, 40, sourceInFrame: 5);
        var b = p.Video(v1, tone, 40, 80, sourceInFrame: 50);
        v1.Transitions.Add(new Transition { TransitionTypeId = TransitionRules.CrossDissolve, Duration = p.F(10), LeftClipId = a.Id, RightClipId = b.Id });
        (a.FadeIn, a.FadeOut) = (p.F(4), p.F(8));   // the fade out sits on the dissolve's edge: not applied (D025 PO-8)
        b.FadeIn = p.F(6);                          // likewise

        await AssertUnlinkChangesNothing(p, "unlink-dissolve", a, b);
        var sounds = p.Project.Timeline.AudioTracks[0].Clips.Cast<AudioClip>().ToList();
        Assert.Equal((p.F(4), MediaTime.Zero), (sounds[0].FadeIn, sounds[0].FadeOut));
        Assert.Equal(MediaTime.Zero, sounds[1].FadeIn);
    }

    [FfmpegFact]
    public async Task Unlink_audio_on_a_muted_video_track_stays_silent()
    {
        var p = new ProjectBuilder(FrameRate.Fps25);
        var v1 = p.VideoTrack();
        var music = p.AudioTrack();
        p.Audio(music, _media.Tone(), 0, 50);       // other sound, on its own track: something to hear
        var clip = p.Video(v1, _media.PatternWithTone(), 0, 50, sourceInFrame: 2);
        v1.IsMuted = true;

        await AssertUnlinkChangesNothing(p, "unlink-muted", clip);
        Assert.True(p.Project.Timeline.AudioTracks.SelectMany(t => t.Clips).OfType<AudioClip>().Single(c => c.MediaAssetId == clip.MediaAssetId).IsMuted);
    }

    private sealed class SceneProject(Core.Entities.Project project) : IProjectService
    {
        public Core.Entities.Project Current { get; } = project;
        public event EventHandler? ProjectChanged { add { } remove { } }
        public event EventHandler? MediaAssetsChanged { add { } remove { } }
        public event EventHandler? TimelineChanged { add { } remove { } }
        public event EventHandler? SaveStateChanged { add { } remove { } }
        public event EventHandler? ProjectSaved { add { } remove { } }
        public event EventHandler<MediaAvailabilityChangedEventArgs>? MediaAvailabilityChanged { add { } remove { } }
        public event EventHandler<MediaRelinkedEventArgs>? MediaRelinked { add { } remove { } }
        public Core.Entities.Project CreateNew(string name, ProjectSettings? settings = null) => throw new NotSupportedException();
        public Task<Core.Entities.Project> OpenAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Core.Entities.Project> RestoreRecoveryAsync(string recoveryFilePath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveAsAsync(string projectFolderPath, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecheckMediaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void NotifyMediaRelinked(IReadOnlyList<MediaFileReplacement> replacements) { }
        public MediaAddResult AddMediaAssets(IEnumerable<MediaAsset> assets) => throw new NotSupportedException();
        public void NotifyMediaAssetsChanged() { }
        public void NotifyTimelineChanged() { }
    }
}
