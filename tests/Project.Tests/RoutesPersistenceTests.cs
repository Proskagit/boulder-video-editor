using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Project.Persistence;
using Xunit;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D053: <c>project.json</c> v15 stores a video clip's routes (each markup with its id), the route of an anchored template
/// and its FIT climb — saved and opened exactly; a v14 file's markup is read as the clip's one route (its id the clip's,
/// its templates anchored to it), a template of a clip without markup gets a route of its own (its green only); a route
/// without an id or two routes with one id refuse the file; a template anchored to a route without markup is valid.
/// </summary>
public class RoutesPersistenceTests
{
    private const string Folder = @"C:\Projects\Routes";
    private static readonly FrameRate Rate = FrameRate.Fps25;

    private static Core.Entities.Project Load(string json) => ProjectSerializer.Deserialize(json, Folder, _ => true);
    private static string Save(Core.Entities.Project project) => ProjectSerializer.Serialize(project, Folder);
    private static JsonObject ClipJson(JsonNode root) => root["timeline"]!["videoTracks"]![0]!["clips"]![0]!.AsObject();

    private static HoldMarkup Route(double x, HoldAnimation? animation = null) => RouteColor.WithResolved(new HoldMarkup
    {
        Holds = ImmutableArray.Create(new Hold(Guid.NewGuid(), ImmutableArray.Create(new NormalizedPoint(x, 0.1),
            new NormalizedPoint(x + 0.1, 0.1), new NormalizedPoint(x + 0.05, 0.2)), new OkLabColor(0.6, 0.2, 0.1), new OkLabColor(0.9, 0, 0), MediaTime.Zero)),
        Animation = animation
    });

    private static TemplateClip Plate(long start, long end, Guid clip, Guid? route, ClimbIdentity? climb = null) => new()
    {
        Kind = TemplateKind.Boulder, TimelineStart = MediaTime.FromFrame(start, Rate), Duration = MediaTime.FromFrame(end - start, Rate),
        StatusAt = MediaTime.FromFrame(end - start - 50, Rate), Route = "7", Grade = "6B", AnchorClipId = clip, AnchorRouteId = route, FitClimb = climb
    };

    private static (Core.Entities.Project Project, VideoClip Clip) Build(ImmutableArray<HoldMarkup> routes, params Func<VideoClip, TemplateClip>[] plates)
    {
        var project = new Core.Entities.Project
        {
            Name = "Routes",
            Settings = new ProjectSettings { FrameWidth = 1920, FrameHeight = 1080, FrameRate = Rate, IsFrameRateLocked = true, AudioSampleRate = 48000 }
        };
        var asset = new MediaAsset
        {
            FilePath = Folder + @"\media\climb.mp4", Kind = MediaKind.Video, AnalysisStatus = MediaAnalysisStatus.Completed,
            Metadata = new MediaMetadata { Duration = MediaTime.FromSeconds(100), FrameRate = Rate }
        };
        project.MediaAssets.Add(asset);
        var clip = new VideoClip
        {
            MediaAssetId = asset.Id, TimelineStart = MediaTime.Zero, Duration = MediaTime.FromFrame(1500, Rate), SourceIn = MediaTime.Zero,
            SourceOut = MediaTime.FromFrame(1500, Rate), Routes = routes
        };
        var v1 = new Track { Type = TrackType.Video, Name = "V1", Order = 0 };
        v1.Clips.Add(clip);
        var v2 = new Track { Type = TrackType.Video, Name = "V2", Order = 1 };
        foreach (var plate in plates) v2.Clips.Add(plate(clip));
        project.Timeline.VideoTracks.Add(v1);
        project.Timeline.VideoTracks.Add(v2);
        project.Timeline.AudioTracks.Add(new Track { Type = TrackType.Audio, Name = "A1" });
        return (project, clip);
    }

    private static readonly HoldAnimation Orange = new()
    {
        Mode = HoldAnimationMode.Duration, StartSourceTime = MediaTime.FromSeconds(5), Duration = MediaTime.FromSeconds(5)
    };

    [Fact]
    public void Several_routes_their_templates_and_a_fit_climb_survive_a_save_open_save_exactly_as_version_15()
    {
        var a = Route(0.1, Orange);
        var b = Route(0.6, Orange with { StartSourceTime = MediaTime.FromSeconds(35) });
        var climb = new ClimbIdentity(3, 2, new DateTimeOffset(2026, 9, 27, 7, 10, 0, TimeSpan.Zero));
        var greenOnly = Guid.NewGuid();
        var (project, clip) = Build(ImmutableArray.Create(a, b),
            c => Plate(75, 400, c.Id, a.Id),
            c => Plate(825, 1300, c.Id, b.Id),
            c => Plate(1350, 1500, c.Id, greenOnly, climb));   // a FIT-made route: its green only

        var first = Save(project);
        var root = JsonNode.Parse(first)!;
        Assert.Equal(19, root["formatVersion"]!.GetValue<int>());
        Assert.Null(ClipJson(root)["holdMarkup"]);
        Assert.Equal(new[] { a.Id.ToString(), b.Id.ToString() }, ClipJson(root)["routes"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));

        var loaded = Load(first);
        var video = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[0];
        Assert.Equal(new[] { a.Id, b.Id }, video.Routes.Select(r => r.Id));
        Assert.True(video.Routes[1].SameAs(b));
        var templates = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().ToList();
        Assert.Equal(new Guid?[] { a.Id, b.Id, greenOnly }, templates.Select(t => t.AnchorRouteId));
        Assert.Equal(climb, templates[2].FitClimb);
        Assert.Null(RouteOverlays.RouteOf(templates[2], video));   // a route with its green only
        Assert.Equal(first, Save(loaded));
    }

    [Fact]
    public void A_version_14_markup_is_the_clips_one_route_and_its_templates_belong_to_it()
    {
        var (project, clip) = Build(ImmutableArray.Create(Route(0.1, Orange)), c => Plate(75, 400, c.Id, null), c => Plate(400, 900, c.Id, null));
        var v14 = LegacyJson.As(Save(project), 14);
        Assert.Contains("\"holdMarkup\"", v14);

        var loaded = Load(v14);
        var video = (VideoClip)loaded.Timeline.VideoTracks[0].Clips[0];
        var route = Assert.Single(video.Routes);
        Assert.Equal(video.Id, route.Id);                                         // the same file opened twice: the same route
        Assert.All(loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>(), t => Assert.Equal(route.Id, t.AnchorRouteId));
        Assert.Equal(route.Id, ((VideoClip)Load(v14).Timeline.VideoTracks[0].Clips[0]).Routes[0].Id);
        Assert.Equal(19, JsonNode.Parse(Save(loaded))!["formatVersion"]!.GetValue<int>());
    }

    [Fact]
    public void A_version_14_template_of_a_clip_without_markup_gets_a_route_of_its_own()
    {
        var (project, _) = Build(ImmutableArray<HoldMarkup>.Empty, c => Plate(75, 400, c.Id, null), c => Plate(400, 900, c.Id, null));
        var loaded = Load(LegacyJson.As(Save(project), 14));
        var templates = loaded.Timeline.VideoTracks[1].Clips.OfType<TemplateClip>().ToList();
        Assert.NotNull(templates[0].AnchorRouteId);
        Assert.Equal(templates[0].AnchorRouteId, templates[1].AnchorRouteId);    // one route (the one markup the clip had)
        Assert.Empty(((VideoClip)loaded.Timeline.VideoTracks[0].Clips[0]).Routes);
    }

    [Fact]
    public void A_route_without_an_id_or_two_routes_with_one_id_refuse_the_file()
    {
        var (project, _) = Build(ImmutableArray.Create(Route(0.1), Route(0.5)));
        var noId = JsonNode.Parse(Save(project))!;
        ClipJson(noId)["routes"]![0]!.AsObject().Remove("id");
        Assert.Throws<ProjectFileException>(() => Load(noId.ToJsonString()));

        var twice = JsonNode.Parse(Save(project))!;
        ClipJson(twice)["routes"]![1]!["id"] = ClipJson(twice)["routes"]![0]!["id"]!.GetValue<string>();
        Assert.Throws<ProjectFileException>(() => Load(twice.ToJsonString()));
    }
}
