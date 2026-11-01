using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>D049 §3: only ISO-8601 with <c>Z</c> or a numeric offset is an instant; anything else is «unrecognised», never
/// read as UTC; no assumed zone, invariant culture.</summary>
public class RecordingTimeTagTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 27, 7, 35, 7, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-09-27T07:35:07.000000Z")]   // the phone's tag, as ffprobe returns it
    [InlineData("2026-09-27T07:35:07Z")]
    [InlineData("2026-09-27T10:35:07+03:00")]
    [InlineData("2026-09-27T10:35:07+0300")]
    [InlineData("2026-09-27T02:05:07-05:30")]
    [InlineData("2026-09-27T21:35:07+14:00")]
    [InlineData("2026-09-26T21:35:07-10:00")]
    public void An_instant_with_a_zone_designator_is_read_as_that_instant(string tag)
    {
        var parsed = RecordingTimeTag.Parse(tag);
        Assert.Equal(Instant, parsed);
        Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
    }

    [Fact]
    public void Fractions_are_kept_to_the_tick_and_cut_off_beyond_it()
    {
        Assert.Equal(Instant.AddTicks(1_234_567), RecordingTimeTag.Parse("2026-09-27T07:35:07.1234567Z"));
        Assert.Equal(Instant.AddTicks(1_234_567), RecordingTimeTag.Parse("2026-09-27T07:35:07.123456789Z"));
        Assert.Equal(Instant.AddTicks(5_000_000), RecordingTimeTag.Parse("2026-09-27T07:35:07.5Z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-09-27T07:35:07")]           // no designator: never read as UTC
    [InlineData("2026-09-27 07:35:07Z")]          // a space for T
    [InlineData("2026-09-27T07:35:07z")]
    [InlineData(" 2026-09-27T07:35:07Z")]
    [InlineData("2026-09-27T07:35:07 Z")]
    [InlineData("2026-09-27T07:35:07+3")]
    [InlineData("2026-09-27T07:35:07−03:00")] // a Unicode minus
    [InlineData("2026-09-27T07:35:07+15:00")]     // beyond ±14 h
    [InlineData("2026-02-30T07:35:07Z")]
    [InlineData("2026-09-27T24:00:00Z")]
    [InlineData("2026-09-27T07:35:60Z")]
    [InlineData("1904-01-01T00:00:00Z")]          // the QuickTime epoch
    [InlineData("1970-01-01T00:00:00Z")]          // the Unix epoch
    [InlineData("1999-12-31T23:59:59Z")]
    [InlineData("2000-01-01T02:59:59+03:00")]     // before 2000-01-01 as an instant
    [InlineData("27.09.2026 07:35:07")]
    [InlineData("not a date")]
    public void Anything_else_is_unrecognised(string? tag) => Assert.Null(RecordingTimeTag.Parse(tag));

    [Fact]
    public void The_earliest_accepted_instant_is_2000_01_01_utc() =>
        Assert.Equal(RecordingTimeTag.Earliest, RecordingTimeTag.Parse("2000-01-01T03:00:00+03:00"));
}
