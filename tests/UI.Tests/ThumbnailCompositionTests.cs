using System.Reflection;
using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Thumbnails;
using AiVideoEditor.Project;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.4b: the app's own composition root provides the thumbnail services, and there is one thumbnail path
/// model — <c>&lt;project&gt;/cache/thumbnails</c> and <c>…\cache\unsaved\&lt;id&gt;\thumbnails</c> — with no other
/// thumbnails folder left in <see cref="AppPaths"/>. Resolving does no I/O: nothing is created on this machine.
/// </summary>
public sealed class ThumbnailCompositionTests
{
    [Fact]
    public void The_app_composition_resolves_the_thumbnail_service_and_the_cache_location()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddAiVideoEditor();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ThumbnailService>(provider.GetRequiredService<IThumbnailService>());
        Assert.Same(provider.GetRequiredService<IThumbnailService>(), provider.GetRequiredService<IThumbnailService>());
        var location = Assert.IsType<ThumbnailCacheLocation>(provider.GetRequiredService<IThumbnailCacheLocation>());

        var projects = provider.GetRequiredService<IProjectService>();
        var expected = Path.Combine(AppPaths.AppDataRoot, "cache", "unsaved", projects.Current.Id.ToString("N"), "thumbnails");
        Assert.Equal(expected, location.CurrentFolder); // a new project is unsaved
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "cache", "unsaved"), AppPaths.UnsavedCacheRoot);
    }

    [Fact]
    public void AppPaths_no_longer_offers_a_competing_project_thumbnails_folder()
    {
        var members = typeof(AppPaths).GetMembers(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).ToList();

        Assert.DoesNotContain("ProjectThumbnailsFolder", members);
        Assert.Equal(Path.Combine("P", "cache", "thumbnails"), ThumbnailCacheLocation.SavedFolder("P"));
    }
}
