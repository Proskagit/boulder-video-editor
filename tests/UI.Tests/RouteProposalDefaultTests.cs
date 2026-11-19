using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D072 amendment 2: «Предлагать трассу» is off by default; the user's choice is kept in the settings.</summary>
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
    public void A_fresh_user_has_the_route_proposals_off()
    {
        Assert.False(Markup(new InMemoryUserSettings()).IsRouteProposalEnabled);
        Assert.False(new UserSettings().RouteProposalsEnabled);
    }

    [Fact]
    public void Turning_them_on_is_kept_for_the_next_start()
    {
        var settings = new InMemoryUserSettings();
        Markup(settings).IsRouteProposalEnabled = true;
        Assert.True(settings.Current.RouteProposalsEnabled);
        Assert.True(Markup(new InMemoryUserSettings(settings.Current)).IsRouteProposalEnabled);

        Markup(settings).IsRouteProposalEnabled = false;
        Assert.False(Markup(new InMemoryUserSettings(settings.Current)).IsRouteProposalEnabled);
    }
}
