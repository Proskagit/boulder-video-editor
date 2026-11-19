using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D072 §3: «Предлагать трассу» is on by default; the user's choice is kept in the settings.</summary>
public class RouteProposalDefaultTests
{
    private static HoldMarkupViewModel Markup(IUserSettingsService settings)
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        return new HoldMarkupViewModel(edit, projects, new StatusService(), settings: settings);
    }

    [Fact]
    public void A_fresh_user_has_the_route_proposals_on()
    {
        Assert.True(Markup(new InMemoryUserSettings()).IsRouteProposalEnabled);
        Assert.True(new UserSettings().RouteProposalsEnabled);
    }

    [Fact]
    public void Turning_them_off_is_kept_for_the_next_start()
    {
        var settings = new InMemoryUserSettings();
        Markup(settings).IsRouteProposalEnabled = false;
        Assert.False(settings.Current.RouteProposalsEnabled);
        Assert.False(Markup(new InMemoryUserSettings(settings.Current)).IsRouteProposalEnabled);

        Markup(settings).IsRouteProposalEnabled = true;
        Assert.True(Markup(new InMemoryUserSettings(settings.Current)).IsRouteProposalEnabled);
    }
}
