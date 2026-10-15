using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// Phase 19 Step 19.1 (D034 §6, §7): <c>project.json</c> v5 stores a video clip's hold markup. The required serialization
/// test of the colour samples: every hold's <c>SurfaceLab</c>, <c>WallLab</c> and <c>SourceTime</c> survive a save / open
/// / save exactly (holds from different markup frames, non-round values), the second file equals the first, and the Auto
/// colour recomputed from the restored samples alone equals the one before saving; a hold without samples or with
/// non-finite values refuses the file. A v4 file opens without markup and is saved as the current version (v6 since Phase 21,
/// D036); a file newer than the current version is refused.
/// </summary>
public class HoldMarkupPersistenceTests
{
    private const string Folder = @"C:\Projects\Demo";
    private static readonly Func<string, bool> AllExist = _ => true;
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static MediaTime F(long n) => MediaTime.FromFrame(n, Rate);

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, AllExist);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject Json(Core.Entities.Project project) => JsonNode.Parse(Save(project))!.AsObject();
    private static JsonObject ClipJson(JsonObject root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();
    private static ProjectFileException Rejected(JsonNode root) => Assert.Throws<ProjectFileException>(() => Load(root.ToJsonString()));

    private static ImmutableArray<NormalizedPoint> Polygon(double x, double y, double r) => ImmutableArray.Create(
        new NormalizedPoint(x - r, y), new NormalizedPoint(x, y - r * 1.37), new NormalizedPoint(x + r * 0.91, y + r / 3),
        new NormalizedPoint(x + 1e-9, y + r));

    /// <summary>Three holds marked on two different frames, with samples and coordinates that are not round numbers.</summary>
    private static HoldMarkup Markup() => RouteColor.WithResolved(new HoldMarkup
    {
        StrokeWidth1080 = 4.375,
        Holds = ImmutableArray.Create(
            new Hold(Guid.NewGuid(), Polygon(0.1234567890123, 0.4567890123456, 0.0123456789),
                new OkLabColor(0.62779838541234567, 0.22487310987654321, 0.12541698765432109),
                new OkLabColor(0.83211234567890123, 0.01234567890123457, 0.0456789012345678), new MediaTime(12_345_678)),
            new Hold(Guid.NewGuid(), Polygon(0.7, 0.3, 0.05),
                new OkLabColor(0.4321, -0.1234567, 0.0987654321), new OkLabColor(0.9, -0.001, 0.002), new MediaTime(12_345_678)),
            new Hold(Guid.NewGuid(), Polygon(0.5, 0.8, 0.02),
                new OkLabColor(1.0 / 3, 2.0 / 7, -1.0 / 11), new OkLabColor(Math.PI / 4, Math.E / 100, -Math.Sqrt(2) / 50), new MediaTime(98_765_432_1)))
    });

    private static Core.Entities.Project Build(HoldMarkup? markup)
    {
        var project = new Core.Entities.Project
        {
            Name = "Holds",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\boulder.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        project.MediaAssets.Add(asset);
        var clip = new VideoClip { MediaAssetId = asset.Id, TimelineStart = F(0), Duration = F(250), SourceIn = F(0), SourceOut = F(250), HoldMarkup = markup };
        var track = new Track { Type = TrackType.Video, Name = "V1" };
        track.Clips.Add(clip);
        project.Timeline.VideoTracks.Add(track);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return project;
    }

    private static HoldMarkup LoadedMarkup(Core.Entities.Project project) =>
        ((VideoClip)project.Timeline.VideoTracks[0].Clips[0]).HoldMarkup!;

    [Fact]
    public void Every_holds_samples_and_time_survive_a_save_open_save_exactly()
    {
        var markup = Markup();
        var first = Save(Build(markup));
        var reopened = Load(first);
        var second = Save(reopened);

        var loaded = LoadedMarkup(reopened);
        Assert.Equal(markup.Holds.Length, loaded.Holds.Length);
        for (var i = 0; i < markup.Holds.Length; i++)
        {
            var (a, b) = (markup.Holds[i], loaded.Holds[i]);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.SurfaceLab.L, b.SurfaceLab.L); Assert.Equal(a.SurfaceLab.A, b.SurfaceLab.A); Assert.Equal(a.SurfaceLab.B, b.SurfaceLab.B);
            Assert.Equal(a.WallLab.L, b.WallLab.L); Assert.Equal(a.WallLab.A, b.WallLab.A); Assert.Equal(a.WallLab.B, b.WallLab.B);
            Assert.Equal(a.SourceTime.Ticks, b.SourceTime.Ticks);
            Assert.Equal(a.Points.ToArray(), b.Points.ToArray());   // exact doubles
        }
        Assert.True(markup.SameAs(loaded));
        Assert.Equal(first, second);                                // the second file identical to the first
        Assert.NotEqual(loaded.Holds[0].SourceTime, loaded.Holds[2].SourceTime);   // holds from different frames
    }

    [Fact]
    public void After_reopening_adding_or_removing_a_hold_recomputes_the_same_auto_colour_from_the_restored_samples()
    {
        var markup = Markup();
        var loaded = LoadedMarkup(Load(Save(Build(markup))));

        // Removing the first hold / adding a new one, before and after the round trip: the Auto result is computed from
        // the stored samples only (no frame anywhere in this test) and is the same.
        var before = RouteColor.Resolve(markup with { Holds = markup.Holds.RemoveAt(0) });
        var after = RouteColor.Resolve(loaded with { Holds = loaded.Holds.RemoveAt(0) });
        Assert.Equal(before, after);

        var extra = new Hold(Guid.NewGuid(), Polygon(0.3, 0.3, 0.03), new OkLabColor(0.95, 0, 0.01), new OkLabColor(0.2, 0.01, 0), new MediaTime(55));
        Assert.Equal(RouteColor.Resolve(markup with { Holds = markup.Holds.Add(extra) }),
                     RouteColor.Resolve(loaded with { Holds = loaded.Holds.Add(extra) }));
        Assert.Equal((markup.ColorHex, markup.KeylineActive), RouteColor.Resolve(loaded));
    }

    [Fact]
    public void The_file_is_the_current_version_and_a_clip_without_markup_writes_nothing()
    {
        var withMarkup = Json(Build(Markup()));
        Assert.Equal(6, (int)withMarkup["formatVersion"]!);
        var hold = ClipJson(withMarkup)["holdMarkup"]!["holds"]![0]!;
        Assert.NotNull(hold["surfaceLab"]!["l"]);
        Assert.NotNull(hold["wallLab"]!["b"]);
        Assert.NotNull(hold["sourceTimeTicks"]);

        Assert.Null(ClipJson(Json(Build(null)))["holdMarkup"]);
    }

    [Fact]
    public void A_version_4_file_opens_without_markup_and_is_saved_as_the_current_version()
    {
        var root = Json(Build(null));
        root["formatVersion"] = 4;
        var project = Load(root.ToJsonString());
        Assert.Null(((VideoClip)project.Timeline.VideoTracks[0].Clips[0]).HoldMarkup);
        Assert.Equal(6, (int)JsonNode.Parse(Save(project))!["formatVersion"]!);

        // A v4 file can't have markup; anything there is ignored, as every older version ignores newer properties.
        var stray = Json(Build(Markup()));
        stray["formatVersion"] = 4;
        Assert.Null(((VideoClip)Load(stray.ToJsonString()).Timeline.VideoTracks[0].Clips[0]).HoldMarkup);
    }

    [Fact]
    public void A_file_newer_than_the_current_version_is_refused()
    {
        var root = Json(Build(Markup()));
        root["formatVersion"] = 7;
        Assert.Contains("newer version", Rejected(root).Message);
    }

    [Theory]
    [InlineData("surfaceLab")]
    [InlineData("wallLab")]
    [InlineData("sourceTimeTicks")]
    [InlineData("points")]
    public void A_hold_without_its_samples_refuses_the_file(string member)
    {
        var root = Json(Build(Markup()));
        ClipJson(root)["holdMarkup"]!["holds"]![1]!.AsObject().Remove(member);
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Theory]
    [InlineData("surfaceLab", "l")]
    [InlineData("wallLab", "a")]
    public void A_hold_with_a_missing_sample_component_refuses_the_file(string sample, string component)
    {
        var root = Json(Build(Markup()));
        ClipJson(root)["holdMarkup"]!["holds"]![0]![sample]!.AsObject().Remove(component);
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Fact]
    public void Non_finite_or_out_of_range_values_refuse_the_file()
    {
        var huge = Json(Build(Markup()));
        ClipJson(huge)["holdMarkup"]!["holds"]![0]!["surfaceLab"]!["l"] = JsonValue.Create(1e308);
        var text = huge.ToJsonString().Replace("1E+308", "1e999");   // overflows to infinity when read
        Assert.Throws<ProjectFileException>(() => Load(text));

        var outside = Json(Build(Markup()));
        ClipJson(outside)["holdMarkup"]!["holds"]![0]!["points"]![0] = 1.5;
        Assert.Contains("damaged", Rejected(outside).Message);

        var negative = Json(Build(Markup()));
        ClipJson(negative)["holdMarkup"]!["holds"]![0]!["sourceTimeTicks"] = -1;
        Assert.Contains("damaged", Rejected(negative).Message);

        var odd = Json(Build(Markup()));
        ClipJson(odd)["holdMarkup"]!["holds"]![0]!["points"]!.AsArray().RemoveAt(0);
        Assert.Contains("damaged", Rejected(odd).Message);
    }

    [Theory]
    [InlineData("strokeWidth1080")]
    [InlineData("colorMode")]
    [InlineData("colorHex")]
    [InlineData("keyline")]
    [InlineData("keylineActive")]
    [InlineData("modelId")]
    [InlineData("holds")]
    public void A_markup_missing_a_member_refuses_the_file(string member)
    {
        var root = Json(Build(Markup()));
        ClipJson(root)["holdMarkup"]!.AsObject().Remove(member);
        Assert.Contains("damaged", Rejected(root).Message);
    }

    [Fact]
    public void Duplicate_hold_ids_and_an_invalid_style_refuse_the_file()
    {
        var dup = Json(Build(Markup()));
        var holds = ClipJson(dup)["holdMarkup"]!["holds"]!.AsArray();
        holds[1]!["id"] = holds[0]!["id"]!.GetValue<Guid>();
        Assert.Contains("damaged", Rejected(dup).Message);

        var width = Json(Build(Markup()));
        ClipJson(width)["holdMarkup"]!["strokeWidth1080"] = 0;
        Assert.Contains("damaged", Rejected(width).Message);

        var color = Json(Build(Markup()));
        ClipJson(color)["holdMarkup"]!["colorHex"] = "magenta";
        Assert.Contains("damaged", Rejected(color).Message);
    }
    [Fact]
    public void A_corrected_contour_survives_a_save_open_save_exactly_as_the_same_hold_with_its_samples()
    {
        // Phase 20 (D035): a correction replaces only the hold's points — the same members (format v5 then, v6 since D036).
        var markup = Markup();
        var original = markup.Holds[0];
        var edit = new Core.Holds.ContourEdit(original.Points, Enumerable.Range(0, original.Points.Length).ToImmutableArray());
        edit = Core.Holds.ContourEditing.MoveHandle(edit, 1, new NormalizedPoint(0.12398765432101, 0.43812345678901), 1920, 1080);
        edit = Core.Holds.ContourEditing.InsertVertex(edit, 2, 0.3141592653589793, 1920, 1080)!;
        var corrected = new Hold(original.Id, edit.Points, original.SurfaceLab, original.WallLab, original.SourceTime);
        var project = Build(markup with { Holds = markup.Holds.SetItem(0, corrected) });

        var first = Save(project);
        var reopened = Load(first);
        Assert.Equal(first, Save(reopened));
        Assert.Equal(6, JsonNode.Parse(first)!["formatVersion"]!.GetValue<int>());

        var loaded = LoadedMarkup(reopened).Holds[0];
        Assert.True(corrected.SameAs(loaded));                                 // id, every point, the samples, the time
        Assert.Equal(edit.Points.Length, loaded.Points.Length);
        Assert.Equal(markup.ColorHex, LoadedMarkup(reopened).ColorHex);
    }
}
