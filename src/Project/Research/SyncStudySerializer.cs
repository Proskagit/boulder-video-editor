using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
            Array(w, "explanations", study.Explanations.OrderBy(e => e.Seq), StudyCanonicalJson.WriteExplanation);
            Array(w, "explanationWithdrawals", study.Withdrawals.OrderBy(x => x.Seq), StudyCanonicalJson.WriteWithdrawal);
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
        try
        {
            return Read(json);
        }
        catch (StudyJsonException ex)
        {
            throw Damaged(ex.Message);
        }
    }

    private static SyncStudy Read(string json)
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
            var schema = StudyJsonReader.Int(root, "schemaVersion");
            if (schema > SyncStudy.CurrentSchemaVersion)
                throw new SyncStudyFileException(SyncStudyFileError.NewerSchema, $"schema {schema} is newer than {SyncStudy.CurrentSchemaVersion}");
            if (schema < 1) throw Damaged($"unknown schema {schema}");

            var study = new SyncStudy(
                StudyJsonReader.Guid(root, "studyId"),
                StudyJsonReader.ReadContent(StudyJsonReader.Obj(root, "draft")),
                StudyJsonReader.Items(root, "locations").Select(StudyJsonReader.ReadLocation),
                StudyJsonReader.Items(root, "revisions").Select(StudyJsonReader.ReadRevision),
                StudyJsonReader.Items(root, "history").Select(StudyJsonReader.ReadHistory),
                StudyJsonReader.Items(root, "evaluations").Select(StudyJsonReader.ReadEvaluation),
                // Step 35.3b2a: absent from a file of Step 35.3b1 — no record then.
                StudyJsonReader.OptItems(root, "explanations").Select(StudyJsonReader.ReadExplanation),
                StudyJsonReader.OptItems(root, "explanationWithdrawals").Select(StudyJsonReader.ReadExplanationWithdrawal));
            var problems = StudyValidator.Problems(study);
            if (problems.Count > 0) throw Damaged(string.Join("; ", problems));
            return study;
        }
    }

    private static SyncStudyFileException Damaged(string detail, Exception? inner = null) =>
        new(SyncStudyFileError.Damaged, detail, inner);

}
