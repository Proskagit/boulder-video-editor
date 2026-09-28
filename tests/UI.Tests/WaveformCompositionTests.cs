using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Media.Waveforms;
using AiVideoEditor.Project;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>
/// D024 Step 9.5b: the app's own composition root provides the waveform service and its cache location — one
/// instance each, the unsaved folder next to the thumbnails' (<c>…\cache\unsaved\&lt;id&gt;\waveforms</c>).
/// Resolving does no I/O: nothing is created on this machine.
/// </summary>
public sealed class WaveformCompositionTests
{
    [Fact]
    public void The_app_composition_resolves_the_waveform_service_and_the_cache_location()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddAiVideoEditor();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WaveformService>(provider.GetRequiredService<IWaveformService>());
        Assert.Same(provider.GetRequiredService<IWaveformService>(), provider.GetRequiredService<IWaveformService>());
        var location = Assert.IsType<WaveformCacheLocation>(provider.GetRequiredService<IWaveformCacheLocation>());
        Assert.Same(location, provider.GetRequiredService<IWaveformCacheLocation>());

        var projects = provider.GetRequiredService<IProjectService>();
        var unsaved = Path.Combine(AppPaths.AppDataRoot, "cache", "unsaved", projects.Current.Id.ToString("N"));
        Assert.Equal(Path.Combine(unsaved, "waveforms"), location.CurrentFolder);
        Assert.Equal(Path.Combine(unsaved, "thumbnails"), provider.GetRequiredService<IThumbnailCacheLocation>().CurrentFolder);
    }
}
