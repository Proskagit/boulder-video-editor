using System.Security.Cryptography;
using System.Text;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using AiVideoEditor.Core.Telemetry.Research;
using Xunit;
using static AiVideoEditor.Core.Tests.Telemetry.Research.StudyFixture;

namespace AiVideoEditor.Core.Tests.Telemetry.Research;

/// <summary>
/// Step 35.3b1: the content hash is the SHA-256 of one canonical JSON — members in ordinal order, collections sorted by
/// key, UTC instants with seven fraction digits, integers only, null members left out, false / 0 written — and covers
/// the observations and decisions only, never the locations or the journal.
/// </summary>
public class StudyCanonicalJsonTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static StudyContent Golden() => new(
        new StudyFit(G(1), Hash('a'), 54_003, new TrainingActivity(T0, T0.AddHours(1), 294, 814),
            new[] { new StudyClimb(0, 1, T0, 201_700_000, T0.AddSeconds(21)) }),
        new[] { new StudyVideo(G(2), "video_20260927_100628.mp4", 10, Hash('b'), 600_000_000, true, "2026-09-27T07:07:00.000000Z", RecordingTimeTagOrigin.Format, 1, null) },
        new[] { new ClimbMark(G(3), G(2), 290_000_000, null, "", 1, null, null, null) },
        new[] { new MatchDecision(G(3), MatchState.Confirmed, new ClimbIdentity(0, 1, T0), null, "", MatchBasis.Manual, null, 1) },
        Array.Empty<Exclusion>());

    [Fact]
    public void The_canonical_json_of_a_known_content_is_exactly_this()
    {
        var expected =
            "{\"decisions\":[{\"basis\":\"manual\",\"climb\":{\"climbKey\":0,\"climbNumber\":1,\"climbStartUtc\":\"2026-09-27T07:02:39.0000000Z\"}," +
            "\"madeInRevision\":1,\"markId\":\"00000000-0000-0000-0000-000000000003\",\"note\":\"\",\"state\":\"confirmed\"}]," +
            "\"exclusions\":[]," +
            "\"fit\":{\"activity\":{\"endUtc\":\"2026-09-27T08:02:39.0000000Z\",\"manufacturer\":294,\"product\":814,\"startUtc\":\"2026-09-27T07:02:39.0000000Z\"}," +
            "\"climbs\":[{\"climbNumber\":1,\"elapsedTicks\":201700000,\"endUtc\":\"2026-09-27T07:03:00.0000000Z\",\"segmentIndex\":0,\"startUtc\":\"2026-09-27T07:02:39.0000000Z\"}]," +
            "\"fitKey\":\"00000000-0000-0000-0000-000000000001\",\"sha256\":\"" + Hash('a') + "\",\"sizeBytes\":54003}," +
            "\"marks\":[{\"addedInRevision\":1,\"markId\":\"00000000-0000-0000-0000-000000000003\",\"note\":\"\",\"startSourceTicks\":290000000,\"videoKey\":\"00000000-0000-0000-0000-000000000002\"}]," +
            "\"videos\":[{\"addedInRevision\":1,\"durationTicks\":600000000,\"originalFileName\":\"video_20260927_100628.mp4\",\"sha256\":\"" + Hash('b') + "\"," +
            "\"sizeBytes\":10,\"tagOrigin\":\"format\",\"tagProbed\":true,\"tagRaw\":\"2026-09-27T07:07:00.000000Z\",\"videoKey\":\"00000000-0000-0000-0000-000000000002\"}]}";

        var bytes = StudyCanonicalJson.ContentBytes(Golden());
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected))).ToLowerInvariant(),
            StudyCanonicalJson.ContentHash(Golden()));
    }

    [Fact]
    public void The_order_of_the_collections_in_memory_does_not_change_the_hash()
    {
        var (study, editor) = Study('a', 'b', 'c');
        foreach (var video in study.Draft.Videos) editor.AddMark(video.VideoKey, 10 * Second, null);
        editor.AddExclusion(study.Identity(1), ExclusionCategory.Other, "x");
        editor.AddExclusion(study.Identity(3), ExclusionCategory.Other, "y");
        var c = study.Draft;
        var shuffled = c with
        {
            Fit = c.Fit with { Climbs = c.Fit.Climbs.Reverse().ToList() },
            Videos = c.Videos.Reverse().ToList(),
            Marks = c.Marks.Reverse().ToList(),
            Exclusions = c.Exclusions.Reverse().ToList(),
        };
        Assert.Equal(StudyCanonicalJson.ContentHash(c), StudyCanonicalJson.ContentHash(shuffled));
    }

    [Fact]
    public void Locations_the_journal_and_offsets_do_not_change_the_hash()
    {
        var (study, editor) = Study();
        var hash = StudyCanonicalJson.ContentHash(study.Draft);
        editor.Relocate(study.FirstVideo(), @"Z:\elsewhere.mp4", 999, "moved");
        editor.Touch(study.FirstVideo(), 12345);
        Assert.Equal(hash, StudyCanonicalJson.ContentHash(study.Draft));

        var golden = Golden();
        var local = golden with { Fit = golden.Fit with { Activity = golden.Fit.Activity with { StartUtc = T0.ToOffset(TimeSpan.FromHours(3)) } } };
        Assert.Equal(StudyCanonicalJson.ContentHash(golden), StudyCanonicalJson.ContentHash(local));   // the same instant
    }

    [Fact]
    public void Any_change_of_an_observation_or_a_decision_changes_the_hash()
    {
        var g = Golden();
        var hash = StudyCanonicalJson.ContentHash(g);
        var variants = new[]
        {
            g with { Marks = new[] { g.Marks[0] with { StartSourceTicks = 290_000_001 } } },
            g with { Videos = new[] { g.Videos[0] with { Sha256 = Hash('c') } } },
            g with { Decisions = new[] { g.Decisions[0] with { State = MatchState.Rejected, Reason = MatchReason.WrongClimb } } },
            g with { Decisions = new[] { g.Decisions[0] with { Climb = new ClimbIdentity(0, 1, T0.AddTicks(1)) } } },
            g with { Videos = new[] { g.Videos[0] with { Retired = new VideoRetirement(2, 9, "lost") } } },
        };
        Assert.All(variants, v => Assert.NotEqual(hash, StudyCanonicalJson.ContentHash(v)));
    }

    [Fact]
    public void A_null_member_is_left_out_and_false_and_zero_are_written()
    {
        var video = new StudyVideo(G(2), "x.mp4", 0, Hash('b'), 0, false, null, null, 1, null);
        var json = StudyCanonicalJson.Value(video);
        Assert.Contains("\"tagProbed\":false", json);
        Assert.Contains("\"durationTicks\":0", json);
        Assert.Contains("\"sizeBytes\":0", json);
        Assert.DoesNotContain("tagRaw", json);
        Assert.DoesNotContain("tagOrigin", json);
        Assert.DoesNotContain("retired", json);
        Assert.DoesNotContain("null", json);
    }
}
