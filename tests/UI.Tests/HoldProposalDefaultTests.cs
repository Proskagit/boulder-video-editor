using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Project;
using AiVideoEditor.Core.Common;
using AiVideoEditor.Timeline;
using AiVideoEditor.UI.Services;
using AiVideoEditor.UI.ViewModels.Panels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D057 §3.15: automatic hold proposals are off by default; the user's choice is kept in the settings.</summary>
public class HoldProposalDefaultTests
{
    private static HoldMarkupViewModel Markup(IUserSettingsService settings)
    {
        var undo = new UndoRedoService();
        var projects = new ProjectService(undo, NullLogger<ProjectService>.Instance);
        var edit = new TimelineEditService(projects, undo, NullLogger<TimelineEditService>.Instance);
        return new HoldMarkupViewModel(edit, projects, new StatusService(), settings: settings);
    }

    [Fact]
    public void A_fresh_user_has_the_proposals_off()
    {
        Assert.False(Markup(new InMemoryUserSettings()).IsProposalGenerationEnabled);
        Assert.False(new UserSettings().HoldProposalsEnabled);
    }

    [Fact]
    public void Turning_them_on_is_kept_for_the_next_start()
    {
        var settings = new InMemoryUserSettings();
        Markup(settings).IsProposalGenerationEnabled = true;
        Assert.True(settings.Current.HoldProposalsEnabled);
        Assert.True(Markup(settings).IsProposalGenerationEnabled);

        Markup(settings).IsProposalGenerationEnabled = false;
        Assert.False(Markup(new InMemoryUserSettings(settings.Current)).IsProposalGenerationEnabled);
    }
}
