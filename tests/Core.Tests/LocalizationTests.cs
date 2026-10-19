using System.Globalization;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Core.Tests;

/// <summary>D039 §1 / §3: the language is never the OS's, and plural forms follow the number of forms.</summary>
public sealed class LocalizationTests
{
    [Theory]
    [InlineData(1, "{0} клип|{0} клипа|{0} клипов", "1 клип")]
    [InlineData(2, "{0} клип|{0} клипа|{0} клипов", "2 клипа")]
    [InlineData(4, "{0} клип|{0} клипа|{0} клипов", "4 клипа")]
    [InlineData(5, "{0} клип|{0} клипа|{0} клипов", "5 клипов")]
    [InlineData(11, "{0} клип|{0} клипа|{0} клипов", "11 клипов")]
    [InlineData(12, "{0} клип|{0} клипа|{0} клипов", "12 клипов")]
    [InlineData(14, "{0} клип|{0} клипа|{0} клипов", "14 клипов")]
    [InlineData(21, "{0} клип|{0} клипа|{0} клипов", "21 клип")]
    [InlineData(22, "{0} клип|{0} клипа|{0} клипов", "22 клипа")]
    [InlineData(111, "{0} клип|{0} клипа|{0} клипов", "111 клипов")]
    [InlineData(0, "{0} клип|{0} клипа|{0} клипов", "0 клипов")]
    [InlineData(1, "{0} clip|{0} clips", "1 clip")]
    [InlineData(0, "{0} clip|{0} clips", "0 clips")]
    [InlineData(21, "{0} clip|{0} clips", "21 clips")]
    [InlineData(3, "{0} of them", "3 of them")]
    public void Plural_picks_the_form_by_the_number_of_forms(long count, string forms, string expected) =>
        Assert.Equal(expected, Plural.Format(count, forms));

    [Fact]
    public void Plural_passes_further_arguments() =>
        Assert.Equal("2 из 5 выбрано", Plural.Format(5, "{1} из {0} выбрано|{1} из {0} выбрано|{1} из {0} выбрано", 2));

    [Fact]
    public void Texts_are_English_without_a_configured_language_whatever_the_OS()
    {
        // The test process never applied a language: the neutral resources, even on a Russian Windows.
        Assert.Equal("{0} clip|{0} clips", Messages.Plural_Clips);
    }

    [Fact]
    public void A_language_scope_reads_the_Russian_resources_and_restores()
    {
        using (UiLanguage.Use("ru"))
            Assert.Equal("{0} клип|{0} клипа|{0} клипов", Messages.Plural_Clips);
        Assert.Equal("{0} clip|{0} clips", Messages.Plural_Clips);
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("RU", "ru")]
    [InlineData(" ru ", "ru")]
    [InlineData("en", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Known_languages_resolve(string? name, string culture) =>
        Assert.Equal(culture, UiLanguage.Resolve(name)!.Name);

    [Fact]
    public void An_unknown_language_is_not_resolved() => Assert.Null(UiLanguage.Resolve("de"));

    [Fact]
    public void Number_formatting_is_not_touched_by_the_language()
    {
        var before = CultureInfo.CurrentCulture;
        using (UiLanguage.Use("ru"))
            Assert.Same(before, CultureInfo.CurrentCulture);
    }
}
