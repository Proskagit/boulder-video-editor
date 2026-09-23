using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Playback;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>Missing-media detection when a project is opened.</summary>
public sealed class OpenMissingMediaTests : IDisposable
{
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private readonly TempFolder _temp = new();
    private readonly UndoRedoService _undo = new();
    private readonly ProjectService _service;

    public OpenMissingMediaTests()
    {
        _service = new ProjectService(_undo, NullLogger<ProjectService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static MediaAsset Video(string path) => new()
    {
        FilePath = path,
        Kind = MediaKind.Video,
        AnalysisStatus = MediaAnalysisStatus.Completed,
        Metadata = new MediaMetadata { Duration = MediaTime.FromFrame(100, Rate), FrameRate = Rate, AvgFrameRate = Rate, Width = 64, Height = 36, VideoCodec = "h264" }
    };

    /// <summary>Writes a project with one V1 clip per asset (placed one after another).</summary>
    private string WriteProject(params MediaAsset[] assets)
    {
        var folder = _temp.Combine("Project");
        var project = new Core.Entities.Project { Name = "Project", Settings = { FrameRate = Rate, IsFrameRateLocked = true } };
        var v1 = new Track { Type = TrackType.Video, Name = "V1" };
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        for (var i = 0; i < assets.Length; i++)
        {
            project.MediaAssets.Add(assets[i]);
            v1.Clips.Add(new VideoClip
            {
                MediaAssetId = assets[i].Id,
                TimelineStart = MediaTime.FromFrame(i * 10, Rate),
                Duration = MediaTime.FromFrame(i * 10 + 10, Rate) - MediaTime.FromFrame(i * 10, Rate),
                SourceIn = MediaTime.Zero,
                SourceOut = MediaTime.FromFrame(i * 10 + 10, Rate) - MediaTime.FromFrame(i * 10, Rate)
            });
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(ProjectFileStore.ProjectFilePath(folder), ProjectSerializer.Serialize(project, folder));
        return folder;
    }

    [Fact]
    public async Task All_media_available_nothing_is_missing()
    {
        var folder = WriteProject(Video(_temp.CreateFile("a.mp4")), Video(_temp.CreateFile("b.mp4")));

        var project = await _service.OpenAsync(folder);

        Assert.All(project.MediaAssets, a => Assert.False(a.IsMissing));
        Assert.False(project.IsDirty);
    }

    [Fact]
    public async Task One_missing_file_is_marked_and_the_project_still_opens()
    {
        var present = Video(_temp.CreateFile("a.mp4"));
        var gone = Video(_temp.Combine("gone.mp4"));
        var folder = WriteProject(present, gone);

        var project = await _service.OpenAsync(folder);

        Assert.Same(project, _service.Current);
        Assert.False(project.MediaAssets.Single(a => a.Id == present.Id).IsMissing);
        Assert.True(project.MediaAssets.Single(a => a.Id == gone.Id).IsMissing);
        Assert.Equal(2, project.Timeline.VideoTracks[0].Clips.Count);
    }

    [Fact]
    public async Task Several_missing_files_are_all_marked()
    {
        var folder = WriteProject(Video(_temp.Combine("x.mp4")), Video(_temp.CreateFile("a.mp4")), Video(_temp.Combine("y.mp4")), Video(_temp.Combine("z.mp4")));

        var project = await _service.OpenAsync(folder);

        Assert.Equal(3, project.MediaAssets.Count(a => a.IsMissing));
        Assert.Equal(new[] { "x.mp4", "y.mp4", "z.mp4" }, project.MediaAssets.Where(a => a.IsMissing).Select(a => a.FileName));
    }

    [Fact]
    public async Task Missing_media_does_not_make_the_project_dirty()
    {
        var folder = WriteProject(Video(_temp.Combine("gone.mp4")));
        var saveStateChanges = 0;
        _service.SaveStateChanged += (_, _) => saveStateChanges++;

        var project = await _service.OpenAsync(folder);

        Assert.False(project.IsDirty);
        Assert.False(_undo.CanUndo);

        // Detecting again (e.g. later on demand) is still not a change to the project.
        _service.DetectMissingMedia();
        Assert.False(project.IsDirty);
        Assert.Equal(1, saveStateChanges); // only the one from Open itself
    }

    [Fact]
    public async Task Missing_media_keeps_its_saved_metadata()
    {
        var gone = Video(_temp.Combine("gone.mp4"));
        var folder = WriteProject(gone);

        var project = await _service.OpenAsync(folder);

        var asset = Assert.Single(project.MediaAssets);
        Assert.True(asset.IsMissing);
        Assert.Equal(MediaAnalysisStatus.Completed, asset.AnalysisStatus);
        Assert.Equal(gone.Metadata!.Duration, asset.Metadata!.Duration);
    }

    [Fact]
    public async Task Missing_state_is_set_before_ProjectChanged_so_the_first_snapshot_is_offline()
    {
        var present = Video(_temp.CreateFile("a.mp4"));
        var gone = Video(_temp.Combine("gone.mp4"));
        var folder = WriteProject(present, gone);
        PlaybackSnapshot? snapshot = null;
        _service.ProjectChanged += (_, _) => snapshot = PlaybackSnapshotBuilder.Build(_service.Current, 1);

        await _service.OpenAsync(folder);

        var spans = Assert.Single(snapshot!.VideoLayers).Spans;
        Assert.Equal(SpanStatus.Video, spans.Single(s => s.AssetId == present.Id).Status);
        var offline = spans.Single(s => s.AssetId == gone.Id);
        Assert.Equal(SpanStatus.Offline, offline.Status);
        Assert.Equal("The media file is missing.", offline.Reason);
    }

    [Fact]
    public async Task Saving_a_project_with_missing_media_keeps_the_references()
    {
        var gone = Video(_temp.Combine("gone.mp4"));
        var folder = WriteProject(gone);
        await _service.OpenAsync(folder);

        await _service.SaveAsync();
        var reopened = await _service.OpenAsync(folder);

        var asset = Assert.Single(reopened.MediaAssets);
        Assert.Equal(gone.FilePath, asset.FilePath);
        Assert.True(asset.IsMissing);
        Assert.NotNull(asset.Metadata);
    }

    [Fact]
    public async Task Media_found_through_the_relative_path_is_not_missing()
    {
        var media = _temp.CreateFile(@"old\Project\media\a.mp4");
        var oldFolder = _temp.Combine("old", "Project");
        var project = new Core.Entities.Project();
        project.Timeline.VideoTracks.Add(new Track { Type = TrackType.Video, Name = "V1" });
        project.MediaAssets.Add(Video(media));
        File.WriteAllText(ProjectFileStore.ProjectFilePath(oldFolder), ProjectSerializer.Serialize(project, oldFolder));
        Directory.CreateDirectory(_temp.Combine("new"));
        Directory.Move(oldFolder, _temp.Combine("new", "Project"));

        var opened = await _service.OpenAsync(_temp.Combine("new", "Project"));

        var asset = Assert.Single(opened.MediaAssets);
        Assert.False(asset.IsMissing);
        Assert.Equal(_temp.Combine("new", "Project", "media", "a.mp4"), asset.FilePath);
    }
}
