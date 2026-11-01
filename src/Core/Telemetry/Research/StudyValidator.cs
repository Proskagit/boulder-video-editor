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
        Content(study.Draft, "draft", study.DraftRevision, study, problems);
        Locations(study, problems);

        var expected = 1;
        foreach (var revision in study.Revisions)
        {
            if (revision.Number != expected++) problems.Add($"revision {revision.Number}: numbers are not 1, 2, 3, …");
            Content(revision.Content, $"revision {revision.Number}", revision.Number, study, problems);
            if (!string.Equals(StudyCanonicalJson.ContentHash(revision.Content), revision.ContentHash, StringComparison.Ordinal))
                problems.Add($"revision {revision.Number}: the content hash does not match its content");
        }

        Increasing(study.History.Select(h => h.Seq), "journal", problems);
        Increasing(study.Evaluations.Select(e => e.Seq), "evaluation records", problems);
        Increasing(study.Explanations.Select(e => e.Seq), "explanations", problems);
        Increasing(study.Withdrawals.Select(w => w.Seq), "explanation withdrawals", problems);
        if (study.AllSeqs().GroupBy(s => s).Any(g => g.Count() > 1))
            problems.Add("two append-only records share a sequence number");
        Records(study, problems);
        return problems;
    }

    private static void Records(SyncStudy study, List<string> problems)
    {
        foreach (var e in study.Evaluations)
        {
            var revision = study.Revisions.FirstOrDefault(r => r.Number == e.Revision);
            if (revision is null) problems.Add($"evaluation {e.Seq}: no revision {e.Revision}");
            else if (!string.Equals(revision.ContentHash, e.RevisionContentHash, StringComparison.Ordinal))
                problems.Add($"evaluation {e.Seq}: the revision's content hash does not match");
            if (string.IsNullOrEmpty(e.Report) || !string.Equals(StudyCanonicalJson.TextHash(e.Report), e.ReportHash, StringComparison.Ordinal))
                problems.Add($"evaluation {e.Seq}: the report hash does not match its report");
            if (string.IsNullOrWhiteSpace(e.AlgorithmVersion) || !IsHash(e.InputsHash)) problems.Add($"evaluation {e.Seq}: no algorithm version or inputs hash");
        }
        Unique(study.Explanations.Select(e => e.ExplanationId), "explanation id", problems);
        foreach (var x in study.Explanations)
        {
            var evaluation = study.Evaluation(x.EvaluationSeq);
            if (evaluation is null) { problems.Add($"explanation {x.ExplanationId}: no evaluation {x.EvaluationSeq}"); continue; }
            if (x.Seq <= evaluation.Seq) problems.Add($"explanation {x.ExplanationId}: recorded before its evaluation");
            if (!string.Equals(x.ReportHash, evaluation.ReportHash, StringComparison.Ordinal)) problems.Add($"explanation {x.ExplanationId}: another report");
            if (string.IsNullOrWhiteSpace(x.Evidence)) problems.Add($"explanation {x.ExplanationId}: no evidence");
            var fit = study.Revisions.FirstOrDefault(r => r.Number == evaluation.Revision)?.Content.Fit;
            if (fit?.Find(x.Climb) is null) problems.Add($"explanation {x.ExplanationId}: the climb is not a climb of the evaluated FIT");
        }
        Unique(study.Withdrawals.Select(w => w.ExplanationId), "withdrawal of an explanation", problems);
        foreach (var w in study.Withdrawals)
        {
            var explanation = study.Explanations.FirstOrDefault(e => e.ExplanationId == w.ExplanationId);
            if (explanation is null) problems.Add($"withdrawal {w.Seq}: no such explanation");
            else if (w.Seq <= explanation.Seq) problems.Add($"withdrawal {w.Seq}: before its explanation");
        }
    }

    /// <summary>The rules of one content; <paramref name="revision"/> is its revision number (the draft's next number): no
    /// record in it can have been added or decided in a later revision.</summary>
    private static void Content(StudyContent c, string where, int revision, SyncStudy study, List<string> problems)
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
            if (v.TagProbeFailed is { } failed && (!failed || v.TagProbed)) problems.Add($"{where}: video {v.VideoKey}: a failed probe beside a probe result");
            if (v.FrameRate is { } fr && (fr.Numerator <= 0 || fr.Denominator <= 0)) problems.Add($"{where}: video {v.VideoKey}: invalid frame rate");
            if (v.AvgFrameRate is { } ar && (ar.Numerator <= 0 || ar.Denominator <= 0)) problems.Add($"{where}: video {v.VideoKey}: invalid average frame rate");
            if (v.RateState == FrameRateState.Unknown) problems.Add($"{where}: video {v.VideoKey}: an unknown frame-rate state is written out");
            if (v.Coverage is { } cov && (cov.State == CoverageState.Unknown || cov.SetInRevision < 1))
                problems.Add($"{where}: video {v.VideoKey}: invalid coverage");
            if (v.Coverage is { } later && later.SetInRevision > revision)
                problems.Add($"{where}: video {v.VideoKey}: coverage set in a later revision");
            if (v.AddedInRevision > revision) problems.Add($"{where}: video {v.VideoKey}: added in a later revision");
            if (v.CoverageStatus == CoverageState.ReviewedNoClimbs
                && c.Marks.Any(m => m.VideoKey == v.VideoKey && m.Withdrawn is null && !(m.Stale is null && c.Decision(m.MarkId) is { IsNotAClimb: true })))
                problems.Add($"{where}: video {v.VideoKey}: reviewed as holding no climb but a mark is unresolved");
        }

        Unique(c.Marks.Select(m => m.MarkId), $"{where}: mark id", problems);
        foreach (var m in c.Marks)
        {
            var video = c.Video(m.VideoKey);
            if (video is null) { problems.Add($"{where}: mark {m.MarkId}: unknown video"); continue; }
            if (m.StartSourceTicks < 0) problems.Add($"{where}: mark {m.MarkId}: negative start");
            if (m.EndSourceTicks is { } end && end <= m.StartSourceTicks) problems.Add($"{where}: mark {m.MarkId}: the end is not after the start");
            if (m.AddedInRevision > revision) problems.Add($"{where}: mark {m.MarkId}: added in a later revision");
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
            if ((d.Basis == MatchBasis.ProposalAccepted) != (d.Proposal is not null)) problems.Add($"{where}: decision {d.MarkId}: proposal reference against its basis");
            if (d.Proposal is { } p && study.Evaluation(p.EvaluationSeq) is null) problems.Add($"{where}: decision {d.MarkId}: the proposal's evaluation is missing");
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
