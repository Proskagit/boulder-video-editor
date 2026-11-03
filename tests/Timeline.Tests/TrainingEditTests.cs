using System.Collections.Concurrent;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Timeline.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// Step 35.4 (D049 §8, §10): the training's clock correction and a video's recording-zero override are one Undo step
/// each; the two are separate values; a video relink keeps the override only for a file recognised as the same recording
/// (size, duration and recording-time tag equal), otherwise the check asks (default: drop), and Undo restores it.
/// </summary>
public sealed class TrainingEditTests
{
    private readonly TimelineFixture _f = new();
    private readonly FakeProbe _probe = new();
    private readonly ConcurrentDictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaRelinkService _relink;
    private static readonly DateTimeOffset Zero = new(2026, 9, 27, 7, 30, 26, TimeSpan.Zero);

    public TrainingEditTests()
    {
        _relink = new MediaRelinkService(_f.Projects, _f.UndoRedo, _probe, NullLogger<MediaRelinkService>.Instance,
            path => _files.TryGetValue(path, out var size) ? size : null);
    }

    private sealed class FakeProbe : IMediaAnalysisService
    {
        public ConcurrentDictionary<string, MediaAnalysisResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<MediaAnalysisResult> AnalyzeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult(Results.TryGetValue(filePath, out var r) ? r : MediaAnalysisResult.Failure(MediaAnalysisOutcome.FileNotFound, "no"));
    }

    private static MediaMetadata Meta(double seconds, string? tag) => new()
    {
        Duration = MediaTime.FromSeconds(seconds), Width = 640, Height = 360, PixelFormat = "yuv420p", VideoCodec = "h264",
        FrameRate = FrameRate.Fps25, AvgFrameRate = FrameRate.Fps25, DisplayRotation = 0, DisplayWidth = 640, DisplayHeight = 360,
        RecordingTimeProbe = tag is null ? RecordingTimeProbe.Absent : RecordingTimeProbe.Found(tag, RecordingTimeTagOrigin.Format)
    };

    private TrainingReference AttachTraining()
    {
        var training = new TrainingReference
        {
            FilePath = @"C:\fit\a.fit", FileSizeBytes = 10, Sha256 = new string('a', 64),
            Activity = new TrainingActivity(Zero, Zero.AddHours(1), 294, 814)
        };
        _f.Projects.SetTraining(training);
        return training;
    }

    [Fact]
    public void The_clock_correction_is_one_undo_step_and_refused_without_a_training()
    {
        Assert.False(_f.Service.SetTrainingClockCorrection(new MediaTime(1)).Success);

        var training = AttachTraining();
        Assert.True(_f.Service.SetTrainingClockCorrection(new MediaTime(2 * TimeSpan.TicksPerSecond)).Success);
        Assert.Equal(new MediaTime(2 * TimeSpan.TicksPerSecond), training.ClockCorrection);

        _f.UndoRedo.Undo();
        Assert.Equal(MediaTime.Zero, training.ClockCorrection);
        _f.UndoRedo.Redo();
        Assert.Equal(new MediaTime(2 * TimeSpan.TicksPerSecond), training.ClockCorrection);
        Assert.True(_f.Service.SetTrainingClockCorrection(new MediaTime(2 * TimeSpan.TicksPerSecond)).NoChange);
    }

    [Fact]
    public void The_override_is_one_undo_step_per_video_and_never_touches_the_clock_correction()
    {
        var training = AttachTraining();
        training.ClockCorrection = new MediaTime(-5);
        var video = _f.Video(100, FrameRate.Fps25);
        var audio = _f.Audio(10);

        Assert.True(_f.Service.SetRecordingZeroOverride(video.Id, Zero.ToOffset(TimeSpan.FromHours(3))).Success);
        Assert.Equal(Zero, video.RecordingZeroOverrideUtc);
        Assert.Equal(TimeSpan.Zero, video.RecordingZeroOverrideUtc!.Value.Offset);   // stored in UTC
        Assert.Equal(new MediaTime(-5), training.ClockCorrection);

        Assert.False(_f.Service.SetRecordingZeroOverride(audio.Id, Zero).Success);    // a video only

        _f.UndoRedo.Undo();
        Assert.Null(video.RecordingZeroOverrideUtc);
        _f.UndoRedo.Redo();
        Assert.Equal(Zero, video.RecordingZeroOverrideUtc);

        Assert.True(_f.Service.SetRecordingZeroOverride(video.Id, null).Success);   // cleared
        Assert.Null(video.RecordingZeroOverrideUtc);
    }

    private async Task<MediaAsset> OfflineOverridden(MediaMetadata metadata, long size)
    {
        var asset = _f.AddAsset("climb.mp4", MediaKind.Video, metadata, MediaAnalysisStatus.Completed);
        asset.FileSizeBytes = size;
        asset.RecordingZeroOverrideUtc = Zero;
        await _f.Projects.RecheckMediaAsync();                             // the file is not there: offline
        Assert.True(asset.IsMissing);
        return asset;
    }

    private string NewFile(string name, MediaMetadata metadata, long size)
    {
        var path = Path.Combine(Path.GetTempPath(), "aive-training-relink", name);
        _files[path] = size;
        _probe.Results[path] = MediaAnalysisResult.Success(metadata);
        return path;
    }

    [Fact]
    public async Task The_same_recording_keeps_the_override_and_undo_restores_the_state()
    {
        var asset = await OfflineOverridden(Meta(99, "2026-09-27T07:35:07.000000Z"), 1000);
        var path = NewFile("moved.mp4", Meta(99, "2026-09-27T07:35:07.000000Z"), 1000);

        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.Equal(RecordingZeroOnRelink.Keep, check.RecordingZero);
        Assert.True(check.KeepRecordingZero);
        Assert.True((await _relink.ApplyAsync(check)).Applied);
        Assert.Equal(Zero, asset.RecordingZeroOverrideUtc);

        _f.UndoRedo.Undo();
        Assert.Equal(Zero, asset.RecordingZeroOverrideUtc);
        Assert.NotEqual(path, asset.FilePath);
    }

    [Theory]
    [InlineData(1001, 99, "2026-09-27T07:35:07.000000Z")]   // size differs
    [InlineData(1000, 98, "2026-09-27T07:35:07.000000Z")]   // duration differs
    [InlineData(1000, 99, "2026-09-27T07:35:08.000000Z")]   // tag differs
    [InlineData(1000, 99, null)]                            // no tag
    public async Task Another_recording_asks_and_by_default_drops_the_override_which_undo_restores(long size, double seconds, string? tag)
    {
        var asset = await OfflineOverridden(Meta(99, "2026-09-27T07:35:07.000000Z"), 1000);
        var path = NewFile("other.mp4", Meta(seconds, tag), size);

        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.Equal(RecordingZeroOnRelink.Ask, check.RecordingZero);
        Assert.False(check.KeepRecordingZero);                            // the default answer
        Assert.True((await _relink.ApplyAsync(check)).Applied);
        Assert.Null(asset.RecordingZeroOverrideUtc);

        _f.UndoRedo.Undo();
        Assert.Equal(Zero, asset.RecordingZeroOverrideUtc);
    }

    [Fact]
    public async Task Keeping_on_the_users_answer_and_no_question_without_an_override()
    {
        var asset = await OfflineOverridden(Meta(99, null), 1000);
        var path = NewFile("other.mp4", Meta(99, null), 1000);
        var check = await _relink.CheckAsync(asset.Id, path);
        Assert.Equal(RecordingZeroOnRelink.Ask, check.RecordingZero);
        check.KeepRecordingZero = true;                                   // «Сохранить»
        Assert.True((await _relink.ApplyAsync(check)).Applied);
        Assert.Equal(Zero, asset.RecordingZeroOverrideUtc);

        var plain = _f.AddAsset("plain.mp4", MediaKind.Video, Meta(50, null), MediaAnalysisStatus.Completed);
        await _f.Projects.RecheckMediaAsync();
        var plainCheck = await _relink.CheckAsync(plain.Id, NewFile("plain2.mp4", Meta(50, null), 5));
        Assert.Equal(RecordingZeroOnRelink.None, plainCheck.RecordingZero);
    }

    [Fact]
    public void The_file_state_of_a_relink_carries_the_override()
    {
        var asset = new MediaAsset { FilePath = "a.mp4", Kind = MediaKind.Video, RecordingZeroOverrideUtc = Zero };
        var state = MediaFileState.Capture(asset);
        asset.RecordingZeroOverrideUtc = null;
        state.ApplyTo(asset);
        Assert.Equal(Zero, asset.RecordingZeroOverrideUtc);
    }
}
