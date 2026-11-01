using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;

namespace AiVideoEditor.Project.Research;

/// <summary>Why a study file was refused.</summary>
public enum SyncStudyFileError
{
    Unreadable,
    Damaged,
    NewerSchema,
    InvalidPath,
}

/// <summary>A study file could not be read, written or trusted; the file on disk is unchanged.</summary>
public sealed class SyncStudyFileException : Exception
{
    public SyncStudyFileException(SyncStudyFileError error, string detail, Exception? inner = null)
        : base(detail, inner) => Error = error;

    public SyncStudyFileError Error { get; }
}

/// <summary>
/// The study file <c>*.syncstudy.json</c> (Step 35.3b1, D050 B5) — never part of a project. Schema 1; a newer schema is
/// refused, unknown members are ignored, a file breaking a rule of <see cref="StudyValidator"/> is damaged. The entities
/// are written by <see cref="StudyCanonicalJson"/> (the same bytes the content hash is taken of, here indented); the file's
/// top level is in a fixed order, its collections sorted by key, so saving an unchanged study gives the same bytes.
/// </summary>
public static class SyncStudySerializer
{
    private static readonly JsonWriterOptions FileOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Serialize(SyncStudy study)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, FileOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", SyncStudy.CurrentSchemaVersion);
            w.WriteString("studyId", StudyCanonicalJson.Key(study.StudyId));
            w.WritePropertyName("draft");
            StudyCanonicalJson.WriteContent(w, study.Draft);
            Array(w, "locations", study.Locations.OrderBy(l => StudyCanonicalJson.Key(l.SubjectKey), StringComparer.Ordinal), StudyCanonicalJson.WriteLocation);
            Array(w, "revisions", study.Revisions.OrderBy(r => r.Number), StudyCanonicalJson.WriteRevision);
            Array(w, "history", study.History.OrderBy(h => h.Seq), StudyCanonicalJson.WriteHistory);
            Array(w, "evaluations", study.Evaluations.OrderBy(e => e.Seq), StudyCanonicalJson.WriteEvaluation);
            w.WriteEndObject();
        }
        // One line ending on every system (a JSON string holds no raw line break, so this touches only the layout).
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static void Array<T>(Utf8JsonWriter w, string name, IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var item in items) write(w, item);
        w.WriteEndArray();
    }

    public static SyncStudy Deserialize(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Damaged("not valid JSON", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Damaged("the root is not an object");
            var schema = Int(root, "schemaVersion");
            if (schema > SyncStudy.CurrentSchemaVersion)
                throw new SyncStudyFileException(SyncStudyFileError.NewerSchema, $"schema {schema} is newer than {SyncStudy.CurrentSchemaVersion}");
            if (schema < 1) throw Damaged($"unknown schema {schema}");

            var study = new SyncStudy(
                Guid(root, "studyId"),
                ReadContent(Obj(root, "draft")),
                Items(root, "locations").Select(ReadLocation),
                Items(root, "revisions").Select(ReadRevision),
                Items(root, "history").Select(ReadHistory),
                Items(root, "evaluations").Select(ReadEvaluation));
            var problems = StudyValidator.Problems(study);
            if (problems.Count > 0) throw Damaged(string.Join("; ", problems));
            return study;
        }
    }

    private static SyncStudyFileException Damaged(string detail, Exception? inner = null) =>
        new(SyncStudyFileError.Damaged, detail, inner);

    // ---- entities --------------------------------------------------------------------------------------------------

    private static StudyContent ReadContent(JsonElement e) => new(
        ReadFit(Obj(e, "fit")),
        Items(e, "videos").Select(ReadVideo).ToList(),
        Items(e, "marks").Select(ReadMark).ToList(),
        Items(e, "decisions").Select(ReadDecision).ToList(),
        Items(e, "exclusions").Select(ReadExclusion).ToList());

    private static StudyFit ReadFit(JsonElement e)
    {
        var a = Obj(e, "activity");
        var product = OptLong(a, "product");
        return new StudyFit(
            Guid(e, "fitKey"), Str(e, "sha256"), Long(e, "sizeBytes"),
            new TrainingActivity(Instant(a, "startUtc"), Instant(a, "endUtc"), Int(a, "manufacturer"), product is { } p ? checked((int)p) : null),
            Items(e, "climbs").Select(c => new StudyClimb(Int(c, "segmentIndex"), Int(c, "climbNumber"), Instant(c, "startUtc"),
                Long(c, "elapsedTicks"), Instant(c, "endUtc"))).ToList());
    }

    private static ClimbIdentity ReadIdentity(JsonElement e) =>
        new(Int(e, "climbKey"), Int(e, "climbNumber"), Instant(e, "climbStartUtc"));

    private static StudyVideo ReadVideo(JsonElement e) => new(
        Guid(e, "videoKey"), Str(e, "originalFileName"), Long(e, "sizeBytes"), Str(e, "sha256"), Long(e, "durationTicks"),
        Bool(e, "tagProbed"), OptStr(e, "tagRaw"), OptEnum<RecordingTimeTagOrigin>(e, "tagOrigin"), Int(e, "addedInRevision"),
        OptObj(e, "retired") is { } r ? new VideoRetirement(Int(r, "revision"), Long(r, "seq"), Str(r, "reason")) : null);

    private static Withdrawal? ReadWithdrawal(JsonElement e) =>
        OptObj(e, "withdrawn") is { } w ? new Withdrawal(Int(w, "revision"), Long(w, "seq")) : null;

    private static Staleness? ReadStaleness(JsonElement e) =>
        OptObj(e, "stale") is { } s ? new Staleness(Int(s, "sinceRevision"), Enum<StaleCause>(s, "cause"), Long(s, "seq")) : null;

    private static ClimbMark ReadMark(JsonElement e) => new(
        Guid(e, "markId"), Guid(e, "videoKey"), Long(e, "startSourceTicks"), OptLong(e, "endSourceTicks"), Str(e, "note"),
        Int(e, "addedInRevision"), ReadWithdrawal(e), ReadStaleness(e), OptInt(e, "reconfirmedInRevision"));

    private static MatchDecision ReadDecision(JsonElement e) => new(
        Guid(e, "markId"), Enum<MatchState>(e, "state"), OptObj(e, "climb") is { } c ? ReadIdentity(c) : null,
        OptEnum<MatchReason>(e, "reason"), Str(e, "note"), Enum<MatchBasis>(e, "basis"), OptStr(e, "proposalRef"),
        Int(e, "madeInRevision"));

    private static Exclusion ReadExclusion(JsonElement e) => new(
        Guid(e, "exclusionId"), ReadIdentity(Obj(e, "climb")), Enum<ExclusionCategory>(e, "category"), Str(e, "reason"),
        Int(e, "recordedInRevision"), ReadWithdrawal(e), ReadStaleness(e), OptInt(e, "reconfirmedInRevision"));

    private static StudyLocation ReadLocation(JsonElement e) =>
        new(Guid(e, "subjectKey"), Str(e, "currentPath"), OptLong(e, "lastWriteUtcTicks"));

    private static StudyRevision ReadRevision(JsonElement e) =>
        new(Int(e, "number"), Instant(e, "frozenAtUtc"), Str(e, "contentHash"), Str(e, "note"), ReadContent(Obj(e, "content")));

    private static HistoryEntry ReadHistory(JsonElement e) => new(
        Long(e, "seq"), Int(e, "revision"), Instant(e, "atUtc"), Enum<HistoryKind>(e, "kind"), OptGuid(e, "subjectId"),
        OptStr(e, "old"), OptStr(e, "new"), Str(e, "note"));

    private static EvaluationRecord ReadEvaluation(JsonElement e) =>
        new(Long(e, "seq"), Int(e, "revision"), Str(e, "algorithmVersion"), Str(e, "inputsHash"), Instant(e, "atUtc"));

    // ---- primitives ------------------------------------------------------------------------------------------------

    private static JsonElement? Member(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Damaged($"an object was expected for «{name}»");
        return e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    }

    private static JsonElement Required(JsonElement e, string name) => Member(e, name) ?? throw Damaged($"«{name}» is missing");

    private static JsonElement Obj(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Object } o ? o : throw Damaged($"«{name}» is not an object");

    private static JsonElement? OptObj(JsonElement e, string name) =>
        Member(e, name) is { } o ? (o.ValueKind == JsonValueKind.Object ? o : throw Damaged($"«{name}» is not an object")) : null;

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : throw Damaged($"«{name}» is not an array");

    private static string Str(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : throw Damaged($"«{name}» is not a string");

    private static string? OptStr(JsonElement e, string name) => Member(e, name) is null ? null : Str(e, name);

    private static long Long(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : throw Damaged($"«{name}» is not an integer");

    private static long? OptLong(JsonElement e, string name) => Member(e, name) is null ? null : Long(e, name);

    private static int Int(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : throw Damaged($"«{name}» is not an integer");

    private static int? OptInt(JsonElement e, string name) => Member(e, name) is null ? null : Int(e, name);

    private static bool Bool(JsonElement e, string name) => Required(e, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Damaged($"«{name}» is not a boolean"),
    };

    private static Guid Guid(JsonElement e, string name)
    {
        var text = Str(e, name);
        return System.Guid.TryParseExact(text, "D", out var id) && text == StudyCanonicalJson.Key(id) ? id : throw Damaged($"«{name}» is not a key");
    }

    private static Guid? OptGuid(JsonElement e, string name) => Member(e, name) is null ? null : Guid(e, name);

    private static DateTimeOffset Instant(JsonElement e, string name) =>
        DateTime.TryParseExact(Str(e, name), InstantFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : throw Damaged($"«{name}» is not a UTC instant");

    private static T Enum<T>(JsonElement e, string name) where T : struct, System.Enum
    {
        var text = Str(e, name);
        foreach (var value in System.Enum.GetValues<T>())
            if (StudyCanonicalJson.Name(value) == text) return value;
        throw Damaged($"«{name}» has an unknown value");
    }

    private static T? OptEnum<T>(JsonElement e, string name) where T : struct, System.Enum =>
        Member(e, name) is null ? null : Enum<T>(e, name);
}
