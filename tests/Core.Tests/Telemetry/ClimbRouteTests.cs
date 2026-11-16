using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Telemetry;
using Xunit;

namespace AiVideoEditor.Core.Tests.Telemetry;

/// <summary>D062: a FIT route's grade text by the Garmin FIT SDK profile 21.221 and its overlay status.</summary>
public class ClimbRouteTests
{
    [Theory]
    [InlineData(ClimbGradingScale.French, 11, "6b")]     // Garmin Connect «6b» on the export of 2026-11-14
    [InlineData(ClimbGradingScale.French, 13, "6c")]
    [InlineData(ClimbGradingScale.French, 10, "6a+")]
    [InlineData(ClimbGradingScale.French, 0, "1")]
    [InlineData(ClimbGradingScale.French, 29, "9b")]
    [InlineData(ClimbGradingScale.Font, 9, "6B")]        // COROS export of 2026-08-08: 9, 11, 13
    [InlineData(ClimbGradingScale.Font, 11, "6C")]
    [InlineData(ClimbGradingScale.Font, 13, "7A")]
    [InlineData(ClimbGradingScale.Font, 4, "4+")]
    [InlineData(ClimbGradingScale.Font, 25, "9A")]
    [InlineData(ClimbGradingScale.Vermin, 0, "VB")]
    [InlineData(ClimbGradingScale.Vermin, 5, "V4")]
    [InlineData(ClimbGradingScale.Vermin, 18, "V17")]
    [InlineData(ClimbGradingScale.Yds, 10, "5.10a")]
    [InlineData(ClimbGradingScale.Uiaa, 11, "VI+")]
    [InlineData(ClimbGradingScale.Ewbank, 0, "4")]
    [InlineData(ClimbGradingScale.Ewbank, 34, "38")]
    [InlineData(ClimbGradingScale.Dankyu, 7, "1 dan")]
    public void A_grade_value_is_written_as_climbers_write_it_in_its_scale(ClimbGradingScale scale, int value, string text) =>
        Assert.Equal(text, ClimbGrades.Text(scale, value));

    [Theory]
    [InlineData(ClimbGradingScale.French, 30)]
    [InlineData(ClimbGradingScale.Font, 26)]
    [InlineData(ClimbGradingScale.Vermin, 19)]
    [InlineData(ClimbGradingScale.Ewbank, 35)]
    [InlineData(ClimbGradingScale.French, -1)]
    [InlineData(ClimbGradingScale.BritishTechnical, 3)]   // a scale the app does not spell
    [InlineData(ClimbGradingScale.Saxon, 3)]
    public void A_value_outside_its_scale_or_an_unspelled_scale_has_no_text(ClimbGradingScale scale, int value) =>
        Assert.Null(ClimbGrades.Text(scale, value));

    [Theory]
    [InlineData(ClimbResult.Completed, null, BoulderStatus.Top)]   // «Завершено с падениями»
    [InlineData(ClimbResult.Completed, true, BoulderStatus.Top)]   // «Отправить»
    [InlineData(ClimbResult.Success, null, BoulderStatus.Top)]
    [InlineData(ClimbResult.Attempted, null, BoulderStatus.Attempt)]
    [InlineData(ClimbResult.Failure, null, BoulderStatus.Attempt)]
    [InlineData(null, true, BoulderStatus.Top)]
    [InlineData(null, null, BoulderStatus.None)]
    public void The_overlay_status_is_top_for_a_completed_route_and_attempt_for_a_tried_one(ClimbResult? result, bool? send, BoulderStatus status) =>
        Assert.Equal(status, new ClimbRoute(null, null, result, null, send).Status);

    [Fact]
    public void A_route_without_a_scale_or_a_value_has_no_grade()
    {
        Assert.Null(new ClimbRoute(ClimbGradingScale.French, null, null, null, null).Grade);
        Assert.Null(new ClimbRoute(null, 11, null, null, null).Grade);
        Assert.True(new ClimbRoute(null, null, null, null, null).IsEmpty);
    }
}
