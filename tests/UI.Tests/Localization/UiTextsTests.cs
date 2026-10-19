using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Localization;
using AiVideoEditor.UI.Services;
using Xunit;

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>D039 §1: the interface texts are the neutral English in tests and Russian with the language applied.</summary>
public sealed class UiTextsTests
{
    [Fact]
    public void The_status_bar_starts_in_the_configured_language()
    {
        Assert.Equal("Ready.", new StatusService().Message);
        using (UiLanguage.Use("ru"))
            Assert.Equal("Готово.", new StatusService().Message);
    }

    [Fact]
    public void The_UI_texts_are_neutral_without_a_language() => Assert.Equal("Ready.", Strings.Status_Ready);
}
