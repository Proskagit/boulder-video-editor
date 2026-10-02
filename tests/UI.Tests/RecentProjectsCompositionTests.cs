using System.Reflection;
using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project.Persistence;
using AiVideoEditor.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D026 §6, Step 11.7: the app's composition root provides one recent-projects store, kept in the configuration
/// folder, and hands it to <see cref="ProjectFileWorkflow"/>. Resolving does no I/O.</summary>
public sealed class RecentProjectsCompositionTests
{
    [Fact]
    public void The_app_composition_gives_the_workflow_the_store_in_the_configuration_folder()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddAiVideoEditor();
        using var provider = services.BuildServiceProvider();

        var store = Assert.IsType<RecentProjectsStore>(provider.GetRequiredService<IRecentProjectsStore>());
        Assert.Same(store, provider.GetRequiredService<IRecentProjectsStore>());
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "config", "recent-projects.json"), store.FilePath);
        Assert.Equal(AppPaths.RecentProjectsFile, store.FilePath);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "config", RecentProjectsStore.LockFileName), store.LockFilePath);

        var workflow = provider.GetRequiredService<ProjectFileWorkflow>();
        var field = typeof(ProjectFileWorkflow).GetField("_recentProjects", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Same(store, field?.GetValue(workflow));
    }
}
