using AiVideoEditor.UI.Services;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D068 item 4: a status message has a kind — an error never looks like an ordinary message, and the next message
/// replaces the kind together with the text.</summary>
public sealed class StatusKindTests
{
    [Fact]
    public void Each_message_carries_its_kind_until_the_next_one()
    {
        var status = new StatusService();
        Assert.Equal(StatusKind.Info, status.Kind);

        status.ReportError("Failed");
        Assert.Equal("Failed", status.Message);
        Assert.True(status.IsError);
        Assert.False(status.IsWarning);

        status.ReportWarning("Refused");
        Assert.True(status.IsWarning);
        Assert.False(status.IsError);

        status.Report("Saved");
        Assert.Equal(StatusKind.Info, status.Kind);
        Assert.False(status.IsWarning || status.IsError);
    }

    [Fact]
    public void The_kind_is_set_before_the_text_so_a_view_never_shows_the_new_text_in_the_old_colour()
    {
        var status = new StatusService();
        status.ReportError("Failed");
        var kindAtTextChange = StatusKind.Info;
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StatusService.Message)) kindAtTextChange = status.Kind;
        };

        status.ReportWarning("Refused");

        Assert.Equal(StatusKind.Warning, kindAtTextChange);
    }
}
