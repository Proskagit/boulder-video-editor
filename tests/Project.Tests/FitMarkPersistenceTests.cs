using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Color;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D051 §5.2–§5.4, Step 37.3 (T3): <c>project.json</c> v12 stores the training's FIT marks — the video, the kind of the
/// own position and its source time, <c>positionChangedAt</c>, the link — and the declined climbs with or without the
/// former mark, byte for byte; a v11 file opens without them and is saved as v12 differing only in the version; marks in a
/// v11 file are ignored; the uniqueness of marks and links in both directions and the kinds are checked on open; an
/// animation without a link stays valid; recovery carries the same.
/// </summary>
public class FitMarkPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly FrameRate Rate = FrameRate.Fps25;
    private static readonly DateTimeOffset Climbs = new(2026, 9, 27, 7, 10, 0, TimeSpan.Zero);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);

    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);

    private static ClimbIdentity Climb(int i) => new(2 * i, i + 1, Climbs.AddMinutes(i));

    private static HoldMarkup Markup(HoldAnimationMode mode) => RouteColor.WithResolved(new HoldMarkup
    {
        Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(0.1, 0.1),
            new NormalizedPoint(0.2, 0.1), new NormalizedPoint(0.15, 0.2)), new OkLabColor(0.6, 0.2, 0.1), new OkLabColor(0.9, 0, 0), MediaTime.Zero)),
        Animation = mode == HoldAnimationMode.Duration
            ? new HoldAnimation { Mode = HoldAnimationMode.Duration, StartSourceTime = new MediaTime(40_000_000), Duration = new MediaTime(50_000_000) }
            : new HoldAnimation()
    });

    /// <summary>Two videos; clip 0 (video 0) and clip 1 (video 1) with Duration animations, clip 2 with an Interval one,
    /// clip 3 with a Duration animation and no link.</summary>
    private static Core.Entities.Project Build(bool marks)
    {
        var project = new Core.Entities.Project
        {
            Name = "Fit",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var videos = Enumerable.Range(0, 2).Select(i => new MediaAsset
        {
            FilePath = Folder + $@"\media\v{i}.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate },
            RecordingZeroOverrideUtc = Climbs.AddMinutes(-1 + i)
        }).ToList();
        project.MediaAssets.AddRange(videos);
        var track = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        for (var i = 0; i < 4; i++)
            track.Clips.Add(new VideoClip
            {
                MediaAssetId = videos[i == 1 ? 1 : 0].Id, TimelineStart = MediaTime.FromFrame(300 * i, Rate), Duration = MediaTime.FromFrame(300, Rate),
                SourceIn = MediaTime.Zero, SourceOut = MediaTime.FromFrame(300, Rate),
                HoldMarkup = Markup(i == 2 ? HoldAnimationMode.Interval : HoldAnimationMode.Duration)
            });
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });

        var training = new TrainingReference
        {
            FilePath = Folder + @"\fit\Болдеринг.fit", FileSizeBytes = 54003,
            Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            Activity = new TrainingActivity(Climbs.AddMinutes(-8), Climbs.AddHours(1), 294, 814),
            ClockCorrection = new MediaTime(-20_000_000)
        };
        project.Training = training;
        if (marks)
        {
            var changed = new DateTimeOffset(2026, 11, 3, 10, 11, 12, 345, TimeSpan.Zero).AddTicks(6789);
            training.Marks.Add(new FitMark(Climb(0), Climbs.AddSeconds(-12), 120_000_000, 0, videos[0].Id, FitMarkPosition.Proposed, changed));
            training.Marks.Add(new FitMark(Climb(1), Climbs.AddMinutes(1).AddTicks(-123_456_789), 123_456_789, 4, videos[0].Id,
                FitMarkPosition.Moved(new MediaTime(987_654_321)), changed.AddMinutes(1), track.Clips[0].Id));
            training.Marks.Add(new FitMark(Climb(2), Climbs.AddMinutes(2).AddSeconds(-14), 140_000_000, 5, videos[1].Id,
                FitMarkPosition.Clamped(MediaTime.Zero), changed.AddMinutes(2), track.Clips[1].Id));
            training.DeclinedClimbs.Add(new DeclinedClimb(Climb(3)));
            training.DeclinedClimbs.Add(new DeclinedClimb(Climb(4), new FitMark(Climb(4), Climbs.AddMinutes(4).AddSeconds(-12), 120_000_000, 0,
                videos[1].Id, FitMarkPosition.Moved(new MediaTime(5)), changed.AddMinutes(4))));
        }
        return project;
    }

    private static JsonObject TrainingJson(JsonNode root) => root["training"]!.AsObject();

    private static string Damage(Action<JsonObject> change)
    {
        var root = JsonNode.Parse(Save(Build(true)))!;
        change(TrainingJson(root));
        return root.ToJsonString();
    }

    private static JsonObject Mark(JsonObject training, int i) => training["fitMarks"]![i]!.AsObject();

    [Fact]
    public void Marks_and_declined_climbs_survive_a_save_open_save_exactly_as_version_12()
    {
        var project = Build(true);
        var first = Save(project);
        var root = JsonNode.Parse(first)!;
        Assert.Equal(16, root["formatVersion"]!.GetValue<int>());
        var training = TrainingJson(root);
        Assert.Equal("proposed", Mark(training, 0)["position"]!.GetValue<string>());
        Assert.Null(Mark(training, 0)["sourceTimeTicks"]);
        Assert.Null(Mark(training, 0)["linkedClipId"]);
        Assert.Equal("moved", Mark(training, 1)["position"]!.GetValue<string>());
        Assert.Equal(987_654_321, Mark(training, 1)["sourceTimeTicks"]!.GetValue<long>());
        Assert.Equal("clamped", Mark(training, 2)["position"]!.GetValue<string>());
        Assert.Null(training["declinedClimbs"]![0]!["formerMark"]);

        var loaded = Load(first);
        Assert.Equal(project.Training!.Marks, loaded.Training!.Marks);
        Assert.Equal(project.Training.DeclinedClimbs, loaded.Training.DeclinedClimbs);
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_training_without_marks_writes_no_mark_members()
    {
        var training = TrainingJson(JsonNode.Parse(Save(Build(false)))!);
        Assert.False(training.ContainsKey("fitMarks"));
        Assert.False(training.ContainsKey("declinedClimbs"));
    }

    [Fact]
    public void A_version_11_file_opens_without_marks_and_is_saved_as_version_12_differing_only_in_the_version()
    {
        var v12 = Save(Build(false));
        var v11 = LegacyJson.As(v12, 11);
        Assert.NotEqual(v12, v11);
        var loaded = Load(v11);
        Assert.Empty(loaded.Training!.Marks);
        Assert.Equal(LegacyJson.Upgraded(v12), Save(loaded));
    }

    [Fact]
    public void Marks_in_a_version_11_file_are_ignored()
    {
        var v11 = LegacyJson.As(Save(Build(true)), 11);
        var loaded = Load(v11);
        Assert.Empty(loaded.Training!.Marks);
        Assert.Empty(loaded.Training.DeclinedClimbs);
    }

    [Fact]
    public void An_animation_without_a_link_round_trips_unchanged_and_is_valid()
    {
        var project = Build(true);
        var unlinked = (VideoClip)project.Timeline.VideoTracks[0].Clips[3];
        var loaded = Load(Save(project));
        var again = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[3];
        Assert.True(again.HoldMarkup!.SameAs(unlinked.HoldMarkup!));
        Assert.DoesNotContain(loaded.Training!.Marks, m => m.LinkedClipId == again.Id);
    }

    [Fact]
    public void Recovery_carries_the_marks()
    {
        var text = ProjectSerializer.SerializeRecovery(Build(true), new RecoveryInfo(Folder, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch));
        var (project, _) = ProjectSerializer.DeserializeRecovery(text, _ => true);
        Assert.Equal(3, project.Training!.Marks.Count);
        Assert.Equal(2, project.Training.DeclinedClimbs.Count);
    }

    public static TheoryData<string, Action<JsonObject>> Damages() => new()
    {
        { "two active marks for one climb", t => Mark(t, 1)["climb"] = Mark(t, 0)["climb"]!.DeepClone() },
        { "two animations linked to one climb", t => Mark(t, 1)["climb"] = Mark(t, 2)["climb"]!.DeepClone() },
        { "one animation linked to two climbs", t => Mark(t, 2)["linkedClipId"] = Mark(t, 1)["linkedClipId"]!.DeepClone() },
        { "a link to a missing clip", t => Mark(t, 1)["linkedClipId"] = Guid.NewGuid().ToString() },
        { "a link to a declined climb", t => t["declinedClimbs"]![1]!["formerMark"]!["linkedClipId"] = Mark(t, 1)["linkedClipId"]!.DeepClone() },
        { "a declined climb also active", t => t["declinedClimbs"]![0]!["climb"] = Mark(t, 0)["climb"]!.DeepClone() },
        { "a proposed mark with a source time", t => Mark(t, 0)["sourceTimeTicks"] = 5 },
        { "a moved mark without a source time", t => Mark(t, 1).Remove("sourceTimeTicks") },
        { "a clamped mark without a source time", t => Mark(t, 2).Remove("sourceTimeTicks") },
        { "an unknown kind", t => Mark(t, 0)["position"] = "guessed" },
        { "a mark on a missing video", t => Mark(t, 0)["mediaAssetId"] = Guid.NewGuid().ToString() },
        { "a mark without a climb", t => Mark(t, 0).Remove("climb") },
        { "a former mark of another climb", t => t["declinedClimbs"]![1]!["formerMark"]!["climb"] = Mark(t, 0)["climb"]!.DeepClone() },
    };

    [Theory]
    [MemberData(nameof(Damages))]
    public void A_broken_mark_is_a_damaged_project(string what, Action<JsonObject> change)
    {
        var json = Damage(change);
        var error = Assert.Throws<ProjectFileException>(() => Load(json));
        Assert.False(string.IsNullOrEmpty(error.Message), what);
    }

    [Fact]
    public void A_link_to_a_clip_with_an_interval_animation_is_a_damaged_project()
    {
        var project = Build(true);
        var interval = project.Timeline.VideoTracks[0].Clips[2].Id;
        project.Training!.Marks[1] = project.Training.Marks[1] with { LinkedClipId = interval };
        Assert.Throws<ProjectFileException>(() => Load(Save(project)));
    }
}
