using System.Globalization;
using System.Text.Json;
using AiVideoEditor.Core.Entities;

namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>A study's JSON does not hold what its rules require (the caller reports it as a damaged file).</summary>
public sealed class StudyJsonException : FormatException
{
    public StudyJsonException(string message) : base(message) { }
}

/// <summary>
/// The one reader of the study's JSON entities (Step 35.3R1) — the counterpart of <see cref="StudyCanonicalJson"/>: the
/// study file's loader and the journal's readers (<see cref="MarkHistory"/>) read the same entities with it. Pure: no I/O,
/// no system clock or time zone; a value breaking a rule throws <see cref="StudyJsonException"/>.
/// </summary>
public static class StudyJsonReader
{
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>A journal value (canonical JSON) read with <paramref name="read"/>.</summary>
    public static T Value<T>(string json, Func<JsonElement, T> read)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return read(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new StudyJsonException($"not valid JSON ({ex.Message})");
        }
    }

    private static StudyJsonException Bad(string detail) => new(detail);

    // ---- entities --------------------------------------------------------------------------------------------------

    public static StudyContent ReadContent(JsonElement e) => new(
        ReadFit(Obj(e, "fit")),
        Items(e, "videos").Select(ReadVideo).ToList(),
        Items(e, "marks").Select(ReadMark).ToList(),
        Items(e, "decisions").Select(ReadDecision).ToList(),
        Items(e, "exclusions").Select(ReadExclusion).ToList(),
        OptObj(e, "markingRule") is { } r ? new MarkingRule(Str(r, "id"), Str(r, "text")) : null);

    public static StudyFit ReadFit(JsonElement e)
    {
        var a = Obj(e, "activity");
        var product = OptLong(a, "product");
        return new StudyFit(
            Guid(e, "fitKey"), Str(e, "sha256"), Long(e, "sizeBytes"),
            new TrainingActivity(Instant(a, "startUtc"), Instant(a, "endUtc"), Int(a, "manufacturer"), product is { } p ? checked((int)p) : null),
            Items(e, "climbs").Select(c => new StudyClimb(Int(c, "segmentIndex"), Int(c, "climbNumber"), Instant(c, "startUtc"),
                Long(c, "elapsedTicks"), Instant(c, "endUtc"))).ToList(),
            OptInt(e, "utcOffsetMinutes"));
    }

    public static ClimbIdentity ReadIdentity(JsonElement e) =>
        new(Int(e, "climbKey"), Int(e, "climbNumber"), Instant(e, "climbStartUtc"));

    public static StudyVideo ReadVideo(JsonElement e) => new(
        Guid(e, "videoKey"), Str(e, "originalFileName"), Long(e, "sizeBytes"), Str(e, "sha256"), Long(e, "durationTicks"),
        Bool(e, "tagProbed"), OptStr(e, "tagRaw"), OptEnum<RecordingTimeTagOrigin>(e, "tagOrigin"), Int(e, "addedInRevision"),
        OptObj(e, "retired") is { } r ? new VideoRetirement(Int(r, "revision"), Long(r, "seq"), Str(r, "reason")) : null,
        OptBool(e, "tagProbeFailed"), ReadRate(e, "frameRate"), ReadRate(e, "avgFrameRate"), OptEnum<FrameRateState>(e, "rateState"),
        OptObj(e, "coverage") is { } c ? new VideoCoverage(Enum<CoverageState>(c, "state"), Str(c, "note"), Int(c, "setInRevision")) : null);

    private static StudyRate? ReadRate(JsonElement e, string name) =>
        OptObj(e, name) is { } r ? new StudyRate(Int(r, "numerator"), Int(r, "denominator")) : null;

    private static Withdrawal? ReadWithdrawal(JsonElement e) =>
        OptObj(e, "withdrawn") is { } w ? new Withdrawal(Int(w, "revision"), Long(w, "seq")) : null;

    private static Staleness? ReadStaleness(JsonElement e) =>
        OptObj(e, "stale") is { } s ? new Staleness(Int(s, "sinceRevision"), Enum<StaleCause>(s, "cause"), Long(s, "seq")) : null;

    public static ClimbMark ReadMark(JsonElement e) => new(
        Guid(e, "markId"), Guid(e, "videoKey"), Long(e, "startSourceTicks"), OptLong(e, "endSourceTicks"), Str(e, "note"),
        Int(e, "addedInRevision"), ReadWithdrawal(e), ReadStaleness(e), OptInt(e, "reconfirmedInRevision"),
        OptStr(e, "markingRuleId"), ReadPts(e, "startFramePts"), ReadPts(e, "endFramePts"));

    private static StudyFramePts? ReadPts(JsonElement e, string name) =>
        OptObj(e, name) is { } p ? new StudyFramePts(Long(p, "pts"), Long(p, "timeBaseNumerator"), Long(p, "timeBaseDenominator")) : null;

    public static MatchDecision ReadDecision(JsonElement e)
    {
        // Step 35.3b1 never wrote a proposal reference (no proposal existed); a string one is not trusted.
        if (Member(e, "proposalRef") is not null) throw Bad("a proposal reference in the form of Step 35.3b1");
        return new(
            Guid(e, "markId"), Enum<MatchState>(e, "state"), OptObj(e, "climb") is { } c ? ReadIdentity(c) : null,
            OptEnum<MatchReason>(e, "reason"), Str(e, "note"), Enum<MatchBasis>(e, "basis"),
            OptObj(e, "proposal") is { } p ? new ProposalRef(Long(p, "evaluationSeq"), Str(p, "proposalId")) : null,
            Int(e, "madeInRevision"));
    }

    public static Exclusion ReadExclusion(JsonElement e) => new(
        Guid(e, "exclusionId"), ReadIdentity(Obj(e, "climb")), Enum<ExclusionCategory>(e, "category"), Str(e, "reason"),
        Int(e, "recordedInRevision"), ReadWithdrawal(e), ReadStaleness(e), OptInt(e, "reconfirmedInRevision"));

    public static StudyLocation ReadLocation(JsonElement e) =>
        new(Guid(e, "subjectKey"), Str(e, "currentPath"), OptLong(e, "lastWriteUtcTicks"));

    public static StudyRevision ReadRevision(JsonElement e) =>
        new(Int(e, "number"), Instant(e, "frozenAtUtc"), Str(e, "contentHash"), Str(e, "note"), ReadContent(Obj(e, "content")));

    public static HistoryEntry ReadHistory(JsonElement e) => new(
        Long(e, "seq"), Int(e, "revision"), Instant(e, "atUtc"), Enum<HistoryKind>(e, "kind"), OptGuid(e, "subjectId"),
        OptStr(e, "old"), OptStr(e, "new"), Str(e, "note"));

    public static EvaluationRecord ReadEvaluation(JsonElement e) =>
        new(Long(e, "seq"), Int(e, "revision"), Str(e, "algorithmVersion"), Str(e, "inputsHash"), Instant(e, "atUtc"),
            Str(e, "revisionContentHash"), Str(e, "parameters"), Str(e, "reportHash"), Str(e, "report"));

    public static CaseExplanation ReadExplanation(JsonElement e) =>
        new(Long(e, "seq"), Guid(e, "explanationId"), Long(e, "evaluationSeq"), Str(e, "reportHash"), ReadIdentity(Obj(e, "climb")),
            Enum<ExplanationCategory>(e, "category"), Str(e, "evidence"), Instant(e, "atUtc"));

    public static ExplanationWithdrawal ReadExplanationWithdrawal(JsonElement e) =>
        new(Long(e, "seq"), Guid(e, "explanationId"), Str(e, "note"), Instant(e, "atUtc"));

    // ---- primitives ------------------------------------------------------------------------------------------------

    private static JsonElement? Member(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Bad($"an object was expected for «{name}»");
        return e.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;
    }

    private static JsonElement Required(JsonElement e, string name) => Member(e, name) ?? throw Bad($"«{name}» is missing");

    public static JsonElement Obj(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Object } o ? o : throw Bad($"«{name}» is not an object");

    private static JsonElement? OptObj(JsonElement e, string name) =>
        Member(e, name) is { } o ? (o.ValueKind == JsonValueKind.Object ? o : throw Bad($"«{name}» is not an object")) : null;

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : throw Bad($"«{name}» is not an array");

    public static IEnumerable<JsonElement> OptItems(JsonElement e, string name) =>
        Member(e, name) is null ? Enumerable.Empty<JsonElement>() : Items(e, name);

    private static bool? OptBool(JsonElement e, string name) => Member(e, name) is null ? null : Bool(e, name);

    private static string Str(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : throw Bad($"«{name}» is not a string");

    private static string? OptStr(JsonElement e, string name) => Member(e, name) is null ? null : Str(e, name);

    private static long Long(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : throw Bad($"«{name}» is not an integer");

    private static long? OptLong(JsonElement e, string name) => Member(e, name) is null ? null : Long(e, name);

    public static int Int(JsonElement e, string name) =>
        Required(e, name) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v) ? v : throw Bad($"«{name}» is not an integer");

    private static int? OptInt(JsonElement e, string name) => Member(e, name) is null ? null : Int(e, name);

    private static bool Bool(JsonElement e, string name) => Required(e, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Bad($"«{name}» is not a boolean"),
    };

    public static Guid Guid(JsonElement e, string name)
    {
        var text = Str(e, name);
        return System.Guid.TryParseExact(text, "D", out var id) && text == StudyCanonicalJson.Key(id) ? id : throw Bad($"«{name}» is not a key");
    }

    private static Guid? OptGuid(JsonElement e, string name) => Member(e, name) is null ? null : Guid(e, name);

    private static DateTimeOffset Instant(JsonElement e, string name) =>
        DateTime.TryParseExact(Str(e, name), InstantFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero)
            : throw Bad($"«{name}» is not a UTC instant");

    private static T Enum<T>(JsonElement e, string name) where T : struct, System.Enum
    {
        var text = Str(e, name);
        foreach (var value in System.Enum.GetValues<T>())
            if (StudyCanonicalJson.Name(value) == text) return value;
        throw Bad($"«{name}» has an unknown value");
    }

    private static T? OptEnum<T>(JsonElement e, string name) where T : struct, System.Enum =>
        Member(e, name) is null ? null : Enum<T>(e, name);
}
