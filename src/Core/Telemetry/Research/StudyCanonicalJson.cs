using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>
/// The one JSON form of the study's entities (Step 35.3b1): the canonical bytes behind <see cref="ContentHash"/> and the
/// entity bodies of the study file are written by the same methods. Rules: every object's properties in ordinal order of
/// their camelCase names; collections sorted by key (videos by key, climbs by split index, marks and decisions by mark id,
/// exclusions by id; a GUID as its lower-case "D" string, compared ordinally); instants in UTC as
/// <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>; numbers only as integers (ticks, bytes, numbers) in invariant form; strings as
/// they are (no Unicode normalisation); enums as their lower-case names; a null member is left out, a non-nullable one is
/// always written (also <c>false</c> and <c>0</c>); an empty collection is <c>[]</c>. No floating point anywhere.
/// </summary>
public static class StudyCanonicalJson
{
    /// <summary>The writer options of the canonical bytes: compact, a fixed encoder.</summary>
    public static readonly JsonWriterOptions CanonicalOptions = new() { Indented = false, Encoder = JavaScriptEncoder.Default };

    /// <summary>The SHA-256 (lower-case hex) of the canonical JSON of the content.</summary>
    public static string ContentHash(StudyContent content) =>
        Convert.ToHexString(SHA256.HashData(Bytes(w => WriteContent(w, content)))).ToLowerInvariant();

    /// <summary>The canonical JSON of the content.</summary>
    public static byte[] ContentBytes(StudyContent content) => Bytes(w => WriteContent(w, content));

    /// <summary>The canonical JSON of one value, as the journal stores it.</summary>
    public static string Value(Action<Utf8JsonWriter> write) => System.Text.Encoding.UTF8.GetString(Bytes(write));

    public static string Value(ClimbMark mark) => Value(w => WriteMark(w, mark));
    public static string Value(MatchDecision decision) => Value(w => WriteDecision(w, decision));
    public static string Value(Exclusion exclusion) => Value(w => WriteExclusion(w, exclusion));
    public static string Value(StudyVideo video) => Value(w => WriteVideo(w, video));
    public static string Value(StudyFit fit) => Value(w => WriteFit(w, fit));
    public static string Value(StudyLocation location) => Value(w => WriteLocation(w, location));

    private static byte[] Bytes(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CanonicalOptions)) write(writer);
        return stream.ToArray();
    }

    public static string Instant(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static string Key(Guid id) => id.ToString("D");

    public static string Name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private delegate void Member(Utf8JsonWriter w);

    /// <summary>Writes an object whose members are sorted ordinally by name; a null member is left out.</summary>
    private static void Object(Utf8JsonWriter w, params (string Name, Member? Write)[] members)
    {
        w.WriteStartObject();
        foreach (var (name, write) in members.Where(m => m.Write is not null).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            w.WritePropertyName(name);
            write!(w);
        }
        w.WriteEndObject();
    }

    private static Member Str(string value) => w => w.WriteStringValue(value);
    private static Member? Str(string? value, bool _) => value is null ? null : (Member)(w => w.WriteStringValue(value));
    private static Member Num(long value) => w => w.WriteNumberValue(value);
    private static Member? Num(long? value) => value is { } v ? (Member)(w => w.WriteNumberValue(v)) : null;
    private static Member Bool(bool value) => w => w.WriteBooleanValue(value);
    private static Member At(DateTimeOffset value) => w => w.WriteStringValue(Instant(value));
    private static Member Id(Guid value) => w => w.WriteStringValue(Key(value));
    private static Member? Id(Guid? value) => value is { } v ? (Member)(w => w.WriteStringValue(Key(v))) : null;
    private static Member En<T>(T value) where T : struct, Enum => w => w.WriteStringValue(Name(value));
    private static Member? En<T>(T? value) where T : struct, Enum => value is { } v ? (Member)(w => w.WriteStringValue(Name(v))) : null;

    private static Member Array<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write) => w =>
    {
        w.WriteStartArray();
        foreach (var item in items) write(w, item);
        w.WriteEndArray();
    };

    public static void WriteContent(Utf8JsonWriter w, StudyContent c) => Object(w,
        ("decisions", Array(c.Decisions.OrderBy(d => Key(d.MarkId), StringComparer.Ordinal), WriteDecision)),
        ("exclusions", Array(c.Exclusions.OrderBy(e => Key(e.ExclusionId), StringComparer.Ordinal), WriteExclusion)),
        ("fit", w2 => WriteFit(w2, c.Fit)),
        ("marks", Array(c.Marks.OrderBy(m => Key(m.MarkId), StringComparer.Ordinal), WriteMark)),
        ("videos", Array(c.Videos.OrderBy(v => Key(v.VideoKey), StringComparer.Ordinal), WriteVideo)));

    public static void WriteFit(Utf8JsonWriter w, StudyFit f) => Object(w,
        ("activity", w2 => Object(w2,
            ("endUtc", At(f.Activity.EndUtc)),
            ("manufacturer", Num(f.Activity.Manufacturer)),
            ("product", Num(f.Activity.Product)),
            ("startUtc", At(f.Activity.StartUtc)))),
        ("climbs", Array(f.Climbs.OrderBy(c => c.SegmentIndex), WriteClimb)),
        ("fitKey", Id(f.FitKey)),
        ("sha256", Str(f.Sha256)),
        ("sizeBytes", Num(f.SizeBytes)));

    public static void WriteClimb(Utf8JsonWriter w, StudyClimb c) => Object(w,
        ("climbNumber", Num(c.ClimbNumber)),
        ("elapsedTicks", Num(c.ElapsedTicks)),
        ("endUtc", At(c.EndUtc)),
        ("segmentIndex", Num(c.SegmentIndex)),
        ("startUtc", At(c.StartUtc)));

    public static void WriteIdentity(Utf8JsonWriter w, ClimbIdentity i) => Object(w,
        ("climbKey", Num(i.ClimbKey)),
        ("climbNumber", Num(i.ClimbNumber)),
        ("climbStartUtc", At(i.ClimbStartUtc)));

    public static void WriteVideo(Utf8JsonWriter w, StudyVideo v) => Object(w,
        ("addedInRevision", Num(v.AddedInRevision)),
        ("durationTicks", Num(v.DurationTicks)),
        ("originalFileName", Str(v.OriginalFileName)),
        ("retired", v.Retired is { } r ? (Member)(w2 => Object(w2, ("reason", Str(r.Reason)), ("revision", Num(r.Revision)), ("seq", Num(r.Seq)))) : null),
        ("sha256", Str(v.Sha256)),
        ("sizeBytes", Num(v.SizeBytes)),
        ("tagOrigin", En(v.TagOrigin)),
        ("tagProbed", Bool(v.TagProbed)),
        ("tagRaw", Str(v.TagRaw, true)),
        ("videoKey", Id(v.VideoKey)));

    private static Member? Withdrawn(Withdrawal? x) =>
        x is null ? null : (Member)(w => Object(w, ("revision", Num(x.Revision)), ("seq", Num(x.Seq))));

    private static Member? Stale(Staleness? x) =>
        x is null ? null : (Member)(w => Object(w, ("cause", En(x.Cause)), ("seq", Num(x.Seq)), ("sinceRevision", Num(x.SinceRevision))));

    public static void WriteMark(Utf8JsonWriter w, ClimbMark m) => Object(w,
        ("addedInRevision", Num(m.AddedInRevision)),
        ("endSourceTicks", Num(m.EndSourceTicks)),
        ("markId", Id(m.MarkId)),
        ("note", Str(m.Note)),
        ("reconfirmedInRevision", Num(m.ReconfirmedInRevision)),
        ("stale", Stale(m.Stale)),
        ("startSourceTicks", Num(m.StartSourceTicks)),
        ("videoKey", Id(m.VideoKey)),
        ("withdrawn", Withdrawn(m.Withdrawn)));

    public static void WriteDecision(Utf8JsonWriter w, MatchDecision d) => Object(w,
        ("basis", En(d.Basis)),
        ("climb", d.Climb is { } c ? (Member)(w2 => WriteIdentity(w2, c)) : null),
        ("madeInRevision", Num(d.MadeInRevision)),
        ("markId", Id(d.MarkId)),
        ("note", Str(d.Note)),
        ("proposalRef", Str(d.ProposalRef, true)),
        ("reason", En(d.Reason)),
        ("state", En(d.State)));

    public static void WriteExclusion(Utf8JsonWriter w, Exclusion e) => Object(w,
        ("category", En(e.Category)),
        ("climb", w2 => WriteIdentity(w2, e.Climb)),
        ("exclusionId", Id(e.ExclusionId)),
        ("reason", Str(e.Reason)),
        ("reconfirmedInRevision", Num(e.ReconfirmedInRevision)),
        ("recordedInRevision", Num(e.RecordedInRevision)),
        ("stale", Stale(e.Stale)),
        ("withdrawn", Withdrawn(e.Withdrawn)));

    public static void WriteLocation(Utf8JsonWriter w, StudyLocation l) => Object(w,
        ("currentPath", Str(l.CurrentPath)),
        ("lastWriteUtcTicks", Num(l.LastWriteUtcTicks)),
        ("subjectKey", Id(l.SubjectKey)));

    public static void WriteRevision(Utf8JsonWriter w, StudyRevision r) => Object(w,
        ("content", w2 => WriteContent(w2, r.Content)),
        ("contentHash", Str(r.ContentHash)),
        ("frozenAtUtc", At(r.FrozenAtUtc)),
        ("note", Str(r.Note)),
        ("number", Num(r.Number)));

    public static void WriteHistory(Utf8JsonWriter w, HistoryEntry h) => Object(w,
        ("atUtc", At(h.AtUtc)),
        ("kind", En(h.Kind)),
        ("new", Str(h.New, true)),
        ("note", Str(h.Note)),
        ("old", Str(h.Old, true)),
        ("revision", Num(h.Revision)),
        ("seq", Num(h.Seq)),
        ("subjectId", Id(h.SubjectId)));

    public static void WriteEvaluation(Utf8JsonWriter w, EvaluationRecord e) => Object(w,
        ("algorithmVersion", Str(e.AlgorithmVersion)),
        ("atUtc", At(e.AtUtc)),
        ("inputsHash", Str(e.InputsHash)),
        ("revision", Num(e.Revision)),
        ("seq", Num(e.Seq)));
}
