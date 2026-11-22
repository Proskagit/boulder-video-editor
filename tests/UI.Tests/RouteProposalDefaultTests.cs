using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D072 amendment 2: «Предлагать трассу» is off by default; the user's choice is kept in the settings — D074 §2: until
/// the next new or opened project, which starts with it off (the run's first project keeps the saved value).</summary>
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

    [Fact]
    public async Task A_new_or_an_opened_project_starts_with_them_off()   // D074 §2
    {
        var settings = new InMemoryUserSettings();
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        var markup = new HoldMarkupViewModel(edit, projects, new StatusService(), settings: settings) { IsRouteProposalEnabled = true };

        projects.CreateNew("Next");
        Assert.False(markup.IsRouteProposalEnabled);
        Assert.False(settings.Current.RouteProposalsEnabled);

        var folder = Path.Combine(Path.GetTempPath(), "aive-d074-" + Guid.NewGuid().ToString("N"));
        try
        {
            await projects.SaveAsAsync(folder);
            markup.IsRouteProposalEnabled = true;
            await projects.OpenAsync(folder);
            Assert.False(markup.IsRouteProposalEnabled);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
