using AiVideoEditor.App.Composition;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Infrastructure.Configuration;
using AiVideoEditor.Project.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D051 §6.1, Step 37.2: the app's composition root provides one calibration store, kept in the configuration
/// folder, which also serves the estimate for new proposals. Resolving does no I/O — the user's file is never created by
/// the composition.</summary>
public sealed class FitCalibrationCompositionTests
{
    [Fact]
    public void The_app_composition_gives_one_store_in_the_configuration_folder()
    {
        var existed = File.Exists(AppPaths.FitCalibrationFile);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddAiVideoEditor();
        using var provider = services.BuildServiceProvider();

        var store = Assert.IsType<FitCalibrationStore>(provider.GetRequiredService<IFitCalibrationStore>());
        Assert.Same(store, provider.GetRequiredService<IFitCalibrationStore>());
        Assert.Same(store, provider.GetRequiredService<IStartCorrectionSource>());
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "config", "fit-calibration.json"), store.FilePath);
        Assert.Equal(Path.Combine(AppPaths.AppDataRoot, "config", FitCalibrationStore.LockFileName), store.LockFilePath);
        Assert.Equal(FitCalibrationSnapshot.Initial, store.Current);

        // Step 37.4: the mark actions are the edit service's own instance; 37.6: the FIT settings workflow resolves.
        Assert.Same(provider.GetRequiredService<ITimelineEditService>(), provider.GetRequiredService<IFitMarkEditService>());
        Assert.NotNull(provider.GetRequiredService<AiVideoEditor.UI.Services.FitSettingsWorkflow>());
        // Step 37.5: the save synchronisation is registered — never resolved by a test, since it reads the user's file.
        Assert.Contains(services, d => d.ServiceType == typeof(AiVideoEditor.Project.FitCalibrationSaveSync));
        Assert.Equal(existed, File.Exists(AppPaths.FitCalibrationFile));
    }
}
