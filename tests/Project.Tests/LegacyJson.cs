using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiVideoEditor.Project.Tests;

/// <summary>
/// D053: a file of the current version rewritten as an older version wrote it — before v15 a video clip's single markup
/// was <c>holdMarkup</c> (no id), a template had no route and no FIT climb, a FIT mark no route; before v16 (D055) a
/// «Боулдер» had no heart-rate settings. And the other way: what the current version writes for an older file read back —
/// its one route takes its clip's id.
/// </summary>
internal static class LegacyJson
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string As(string json, int version) => As(JsonNode.Parse(json)!, version).ToJsonString(Indented);

    public static JsonNode As(JsonNode root, int version)
    {
        root["formatVersion"] = version;
        if (version >= 16) return root;
        foreach (var clip in Clips(root))
        {
            // D055 §5: only a «Боулдер» writes showHeartRate; the overlay keeps its look and corner (v13).
            if (clip.Remove("showHeartRate"))
            {
                clip.Remove("heartColorHex");
                clip.Remove("look");
                clip.Remove("corner");
            }
        }
        if (version >= 15) return root;
        foreach (var clip in Clips(root))
        {
            if (clip["routes"] is JsonArray routes)
            {
                clip.Remove("routes");
                if (routes.Count > 0)
                {
                    var markup = routes[0]!.DeepClone().AsObject();
                    markup.Remove("id");
                    clip["holdMarkup"] = markup;
                }
            }
            clip.Remove("anchorRouteId");
            clip.Remove("fitClimb");
        }
        if (root["training"]?["fitMarks"] is JsonArray marks)
            foreach (var mark in marks.OfType<JsonObject>()) mark.Remove("linkedRouteId");
        return root;
    }

    /// <summary>A current file as it is saved again after its older form was read: every clip's first route has the clip's
    /// id, and the templates anchored to the clip name it.</summary>
    public static string Upgraded(string json)
    {
        // Textual, so the file's own formatting stays: each first route's id becomes its clip's id (also where a template
        // names it).
        var root = JsonNode.Parse(json)!;
        foreach (var clip in Clips(root))
            if (clip["routes"] is JsonArray { Count: > 0 } routes)
                json = json.Replace(routes[0]!["id"]!.GetValue<string>(), clip["id"]!.GetValue<string>());
        return json;
    }

    private static IEnumerable<JsonObject> Clips(JsonNode root) =>
        new[] { "videoTracks", "audioTracks" }
            .SelectMany(kind => root["timeline"]?[kind]?.AsArray() ?? new JsonArray())
            .SelectMany(track => track?["clips"]?.AsArray() ?? new JsonArray())
            .OfType<JsonObject>();
}
