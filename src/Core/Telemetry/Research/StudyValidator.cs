namespace AiVideoEditor.Core.Telemetry.Research;

/// <summary>
/// The integrity rules of a study (Step 35.3b1), checked by the loader: a study breaking any of them is damaged. The full
/// climb identity is required only of what is in force — the decisions of active marks and the active exclusions (I10);
/// a withdrawn or stale record keeps its old identity for the history and is not damage. Each frozen revision is checked
/// against its own FIT and its stored content hash (I7).
/// </summary>
public static class StudyValidator
{
    public static IReadOnlyList<string> Problems(SyncStudy study)
    {
        var problems = new List<string>();
        Content(study.Draft, "draft", problems);
        Locations(study, problems);

        var expected = 1;
        foreach (var revision in study.Revisions)
        {
            if (revision.Number != expected++) problems.Add($"revision {revision.Number}: numbers are not 1, 2, 3, …");
            Content(revision.Content, $"revision {revision.Number}", problems);
            if (!string.Equals(StudyCanonicalJson.ContentHash(revision.Content), revision.ContentHash, StringComparison.Ordinal))
                problems.Add($"revision {revision.Number}: the content hash does not match its content");
        }

        Increasing(study.History.Select(h => h.Seq), "journal", problems);
        Increasing(study.Evaluations.Select(e => e.Seq), "evaluation records", problems);
        if (study.History.Select(h => h.Seq).Intersect(study.Evaluations.Select(e => e.Seq)).Any())
            problems.Add("a journal entry and an evaluation record share a sequence number");
        return problems;
    }

    private static void Content(StudyContent c, string where, List<string> problems)
    {
        var fit = c.Fit;
        if (!IsHash(fit.Sha256)) problems.Add($"{where}: the FIT's SHA-256 is malformed");
        if (fit.SizeBytes < 0) problems.Add($"{where}: the FIT's size is negative");
        if (fit.Activity.EndUtc < fit.Activity.StartUtc) problems.Add($"{where}: the activity ends before it starts");
        Unique(fit.Climbs.Select(x => x.SegmentIndex), $"{where}: climb split index", problems);
        Unique(fit.Climbs.Select(x => x.ClimbNumber), $"{where}: climb number", problems);
        if (fit.Climbs.Any(x => x.ElapsedTicks < 0 || x.EndUtc < x.StartUtc)) problems.Add($"{where}: a climb has a negative duration");

        Unique(c.Videos.Select(v => v.VideoKey), $"{where}: video key", problems);
        Unique(c.Videos.Select(v => v.Sha256), $"{where}: video SHA-256", problems);
        foreach (var v in c.Videos)
        {
            if (!IsHash(v.Sha256)) problems.Add($"{where}: video {v.VideoKey}: SHA-256 is malformed");
            if (v.SizeBytes < 0 || v.DurationTicks < 0) problems.Add($"{where}: video {v.VideoKey}: negative size or duration");
            if (string.IsNullOrWhiteSpace(v.OriginalFileName)) problems.Add($"{where}: video {v.VideoKey}: no file name");
            if (!v.TagProbed && (v.TagRaw is not null || v.TagOrigin is not null)) problems.Add($"{where}: video {v.VideoKey}: a tag without a probe");
            if ((v.TagRaw is null) != (v.TagOrigin is null)) problems.Add($"{where}: video {v.VideoKey}: a tag without its origin or the reverse");
            if (v.Retired is { } r && string.IsNullOrWhiteSpace(r.Reason)) problems.Add($"{where}: video {v.VideoKey}: retired without a reason");
        }

        Unique(c.Marks.Select(m => m.MarkId), $"{where}: mark id", problems);
        foreach (var m in c.Marks)
        {
            var video = c.Video(m.VideoKey);
            if (video is null) { problems.Add($"{where}: mark {m.MarkId}: unknown video"); continue; }
            if (m.StartSourceTicks < 0) problems.Add($"{where}: mark {m.MarkId}: negative start");
            if (m.EndSourceTicks is { } end && end <= m.StartSourceTicks) problems.Add($"{where}: mark {m.MarkId}: the end is not after the start");
            // An active mark lies inside its video; a stale one may refer to the video's earlier content.
            if (m.IsActive && video.Retired is null && (m.StartSourceTicks >= video.DurationTicks || m.EndSourceTicks > video.DurationTicks))
                problems.Add($"{where}: mark {m.MarkId}: outside its video");
        }

        Unique(c.Decisions.Select(d => d.MarkId), $"{where}: decision of a mark", problems);
        foreach (var d in c.Decisions)
        {
            var mark = c.Mark(d.MarkId);
            if (mark is null) { problems.Add($"{where}: decision {d.MarkId}: unknown mark"); continue; }
            if ((d.State == MatchState.NotMatched) != (d.Climb is null)) problems.Add($"{where}: decision {d.MarkId}: climb present or missing against its state");
            if (d.State != MatchState.Confirmed && d.Reason is null) problems.Add($"{where}: decision {d.MarkId}: no reason");
            if (d.Reason == MatchReason.Other && string.IsNullOrWhiteSpace(d.Note)) problems.Add($"{where}: decision {d.MarkId}: «other» without a note");
            if (mark.IsActive && d.Climb is { } climb && fit.Find(climb) is null)
                problems.Add($"{where}: decision {d.MarkId}: the climb is not a climb of this FIT (index, number and start)");
        }

        Unique(c.Exclusions.Select(e => e.ExclusionId), $"{where}: exclusion id", problems);
        foreach (var e in c.Exclusions)
        {
            if (string.IsNullOrWhiteSpace(e.Reason)) problems.Add($"{where}: exclusion {e.ExclusionId}: no reason");
            if (e.IsActive && fit.Find(e.Climb) is null)
                problems.Add($"{where}: exclusion {e.ExclusionId}: the climb is not a climb of this FIT (index, number and start)");
        }
    }

    private static void Locations(SyncStudy study, List<string> problems)
    {
        var subjects = study.Draft.Videos.Select(v => v.VideoKey).Prepend(study.Draft.Fit.FitKey).ToHashSet();
        foreach (var location in study.Locations)
        {
            if (!subjects.Contains(location.SubjectKey)) problems.Add($"location {location.SubjectKey}: no such FIT or video");
            if (string.IsNullOrWhiteSpace(location.CurrentPath)) problems.Add($"location {location.SubjectKey}: no path");
        }
        foreach (var group in study.Locations.GroupBy(l => l.SubjectKey).Where(g => g.Count() > 1))
            problems.Add($"location {group.Key}: more than one current location");
        foreach (var subject in subjects.Where(s => study.Locations.All(l => l.SubjectKey != s)))
            problems.Add($"subject {subject}: no location");
    }

    private static bool IsHash(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Unique<T>(IEnumerable<T> values, string what, List<string> problems)
    {
        if (values.GroupBy(v => v).Any(g => g.Count() > 1)) problems.Add($"{what} is not unique");
    }

    private static void Increasing(IEnumerable<long> seqs, string what, List<string> problems)
    {
        long previous = 0;
        foreach (var seq in seqs)
        {
            if (seq <= previous) { problems.Add($"{what}: sequence numbers do not increase"); return; }
            previous = seq;
        }
    }
}
