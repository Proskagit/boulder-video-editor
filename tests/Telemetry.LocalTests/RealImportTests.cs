using System.Globalization;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.Media;
using AiVideoEditor.UI.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiVideoEditor.Telemetry.LocalTests;

/// <summary>Phase Coros C1 on the real export: the file read from disk by the app's import service, and the summary the
/// status bar shows in Russian (device, date, local time, 14 climbs).</summary>
public class RealImportTests
{
    private static readonly string FitPath =
        Environment.GetEnvironmentVariable("AIVE_COROS_FIT") is { Length: > 0 } path
            ? path
            : @"D:\Времянка для монтажа\Боулдер 27.09.2026\Болдеринг20260927100239.fit";

    [Fact]
    public async Task The_real_export_imports_and_reads_as_the_coros_app_shows_it()
    {
        Assert.True(File.Exists(FitPath), $"The COROS export was not found at {FitPath} (set AIVE_COROS_FIT).");

        var workout = await new TrainingImportService(NullLogger<TrainingImportService>.Instance).ImportAsync(FitPath);

        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            using var _ = UiLanguage.Use("ru");
            Assert.Equal("Тренировка импортирована: COROS APEX 4 42mm, 27.09.2026 10:02–11:39 (UTC+03:00), 14 подходов",
                TrainingText.Imported(workout));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
