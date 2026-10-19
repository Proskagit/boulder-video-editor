using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>D039: the edit service's messages and defaults in Russian (the other tests read the neutral English).</summary>
public class RussianMessagesTests
{
    [Fact]
    public void A_new_text_clip_reads_Tekst_and_an_existing_text_is_kept()
    {
        var f = new TimelineFixture();
        using (UiLanguage.Use("ru"))
        {
            var result = f.Service.AddTextClip(MediaTime.Zero);
            Assert.True(result.Success, result.Message);
        }
        var clip = Assert.IsType<TextClip>(Assert.Single(f.V1.Clips));
        Assert.Equal("Текст", clip.Text);

        // The language changes only what is created from now on, never the user's data.
        f.Service.AddTextClip(MediaTime.FromSeconds(10));
        Assert.Equal(new[] { "Текст", "Text" }, f.V1.Clips.OfType<TextClip>().Select(c => c.Text));
    }

    [Fact]
    public void A_refused_edit_on_a_locked_track_is_reported_in_Russian()
    {
        var f = new TimelineFixture();
        Assert.True(f.Service.SetTrackLocked(f.V1.Id, true).Success);
        using (UiLanguage.Use("ru"))
        {
            var result = f.Service.AddTextClip(MediaTime.Zero);
            Assert.False(result.Success);
            Assert.Equal("Дорожка V1 заблокирована.", result.Message);
        }
        Assert.Equal("Track V1 is locked.", f.Service.AddTextClip(MediaTime.Zero).Message);
    }
}
