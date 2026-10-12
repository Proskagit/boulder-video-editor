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
/// Phase 16 Step 16.6 (D031): a timeline made with Insert / Overwrite exports byte for byte like the same timeline laid
/// out directly — the new edits only produce ordinary clips, the rendering rules are unchanged (no new parity criterion).
/// ffmpeg's test pattern (every frame differs), so a wrongly placed source range would show.
/// </summary>
[Collection(EndToEndCollection.Name)]
public sealed class ExportInsertOverwriteEndToEndTests
{
    private readonly E2EMedia _media;

    public ExportInsertOverwriteEndToEndTests(E2EMedia media) => _media = media;

    [FfmpegFact]
    public async Task Insert_exports_like_the_split_and_moved_timeline_laid_out_directly()
    {
        var pattern = _media.Pattern();
        var edited = new ProjectBuilder(FrameRate.Fps25);
        var v1 = edited.VideoTrack();
        edited.Video(v1, pattern, 0, 20);
        var asset = edited.Project.MediaAssets.Single();
        var edit = new TimelineEditService(new SceneProject(edited.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var result = edit.InsertClip(asset.Id, edited.F(50), edited.F(60), edited.F(8));     // source [50, 60) at 8
        Assert.True(result.Success, result.Message);

        var direct = new ProjectBuilder(FrameRate.Fps25);
        var d1 = direct.VideoTrack();
        direct.Video(d1, pattern, 0, 8);
        direct.Video(d1, pattern, 8, 18, sourceInFrame: 50);
        direct.Video(d1, pattern, 18, 30, sourceInFrame: 8);

        await AssertSameExport(edited, direct, "insert");
    }

    [FfmpegFact]
    public async Task Overwrite_exports_like_the_trimmed_timeline_laid_out_directly()
    {
        var pattern = _media.Pattern();
        var edited = new ProjectBuilder(FrameRate.Fps25);
        var v1 = edited.VideoTrack();
        edited.Video(v1, pattern, 0, 12);
        edited.Video(v1, pattern, 12, 24, sourceInFrame: 30);
        var asset = edited.Project.MediaAssets.Single();
        var edit = new TimelineEditService(new SceneProject(edited.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);

        var result = edit.OverwriteClip(asset.Id, edited.F(70), edited.F(78), edited.F(8));  // source [70, 78) over [8, 16)
        Assert.True(result.Success, result.Message);

        var direct = new ProjectBuilder(FrameRate.Fps25);
        var d1 = direct.VideoTrack();
        direct.Video(d1, pattern, 0, 8);
        direct.Video(d1, pattern, 8, 16, sourceInFrame: 70);
        direct.Video(d1, pattern, 16, 24, sourceInFrame: 34);

        await AssertSameExport(edited, direct, "overwrite");
    }

    // Phase 17 Step 17.7 (D032 R7): the sound alone, as an audio clip of the file — the same as inserting the whole file,
    // unlinking its sound and deleting the picture.
    [FfmpegFact]
    public async Task A_sound_only_insert_exports_like_the_whole_file_unlinked_with_its_picture_deleted()
    {
        var take = _media.PatternWithTone();
        var edited = new ProjectBuilder(FrameRate.Fps25);
        edited.Video(edited.VideoTrack(), _media.Solid("000000", FrameRate.Fps25), 0, 60);
        edited.AudioTrack();
        edited.Project.MediaAssets.Add(take);
        var edit = new TimelineEditService(new SceneProject(edited.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        var result = edit.InsertClip(take.Id, edited.F(12), edited.F(42), edited.F(9), soundOnly: true);
        Assert.True(result.Success, result.Message);
        Assert.IsType<AudioClip>(edited.Project.Timeline.AudioTracks[0].Clips.Single());

        var direct = new ProjectBuilder(FrameRate.Fps25);
        var d1 = direct.VideoTrack();
        direct.Video(d1, _media.Solid("000000", FrameRate.Fps25), 0, 60);
        direct.AudioTrack();
        var directTake = _media.PatternWithTone();
        direct.Project.MediaAssets.Add(directTake);
        var directEdit = new TimelineEditService(new SceneProject(direct.Project), new UndoRedoService(), NullLogger<TimelineEditService>.Instance);
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        direct.Project.Timeline.VideoTracks.Add(v2);
        Assert.True(directEdit.InsertClip(directTake.Id, direct.F(12), direct.F(42), direct.F(9), v2.Id).Success);
        var picture = v2.Clips.Single();
        Assert.True(directEdit.UnlinkAudio(picture.Id).Success);
        Assert.True(directEdit.DeleteClips(new[] { picture.Id }).Success);

        var a = await ExportProject(edited.Project, Path.Combine(_media.OutputFolder(), "sound-only-edited.mp4"));
        var b = await ExportProject(direct.Project, Path.Combine(_media.OutputFolder(), "sound-only-direct.mp4"));
        AssertValidMp4(a);
        Assert.True(a.Pcm.AsSpan().SequenceEqual(b.Pcm), "the sound differs");
        Assert.Contains(a.Pcm, s => s != 0);
        Assert.Equal(b.Canvases.Count, a.Canvases.Count);
        for (var i = 0; i < a.Canvases.Count; i++)
            Assert.True(a.Canvases[i].AsSpan().SequenceEqual(b.Canvases[i]), $"frame {i} differs");
        AssertNoFfmpegLeft();
    }

    private async Task AssertSameExport(ProjectBuilder edited, ProjectBuilder direct, string name)
    {
        var a = await ExportProject(edited.Project, Path.Combine(_media.OutputFolder(), $"{name}-edited.mp4"));
        var b = await ExportProject(direct.Project, Path.Combine(_media.OutputFolder(), $"{name}-direct.mp4"));
        AssertValidMp4(a);
        Assert.Equal(b.Canvases.Count, a.Canvases.Count);
        for (var i = 0; i < a.Canvases.Count; i++)
            Assert.True(a.Canvases[i].AsSpan().SequenceEqual(b.Canvases[i]), $"{name}: frame {i} differs from the timeline laid out directly");
        AssertNoFfmpegLeft();
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
