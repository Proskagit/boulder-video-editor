using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using AiVideoEditor.UI.Common;
using AiVideoEditor.UI.Localization;
using Xunit;

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>D039 §2: the interface's own formatting in Russian — units, kinds, drop-down names, plural forms.</summary>
public sealed class RussianTextsTests
{
    [Fact]
    public void File_sizes_use_Russian_units()
    {
        using (UiLanguage.Use("ru"))
        {
            Assert.Equal("842 МБ", FileSizeFormat.ToShortString(842L * 1024 * 1024));
            Assert.Equal("0 Б", FileSizeFormat.ToShortString(0));
        }
        Assert.Equal("842 MB", FileSizeFormat.ToShortString(842L * 1024 * 1024));
    }

    [Fact]
    public void Media_kinds_and_alignments_are_never_enum_names()
    {
        using (UiLanguage.Use("ru"))
        {
            Assert.Equal("ВИДЕО", MediaKindText.Badge(MediaKind.Video));
            Assert.Equal("Аудиофайлы", MediaKindText.FileFilter(MediaKind.Audio));
            Assert.Equal("По центру", AlignmentText.Of(TextAlignment.Center));
        }
        Assert.Equal("Center", AlignmentText.Of(TextAlignment.Center));
    }

    [Theory]
    [InlineData(1, "Выбран 1 клип")]
    [InlineData(3, "Выбрано 3 клипа")]
    [InlineData(11, "Выбрано 11 клипов")]
    [InlineData(21, "Выбран 21 клип")]
    public void The_selection_count_agrees_with_the_number(int count, string expected)
    {
        using (UiLanguage.Use("ru"))
            Assert.Equal(expected, Plural.Format(count, Strings.Selection_Count));
    }

    [Fact]
    public void A_frame_rate_reads_kadr_per_second()
    {
        using (UiLanguage.Use("ru"))
            Assert.Equal("25 к/с", RateFormat.Fps(new Core.Common.FrameRate(25, 1)));
    }
}
