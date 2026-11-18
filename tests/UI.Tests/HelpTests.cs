using AiVideoEditor.UI.Help;
using AiVideoEditor.UI.ViewModels;
using Xunit;

namespace AiVideoEditor.UI.Tests;

/// <summary>D068 §3: the in-app help — its text format, the two languages' files and the window's search.</summary>
public sealed class HelpTests
{
    [Fact]
    public void Topics_subheadings_bullets_and_paragraphs_are_read()
    {
        var topics = HelpDocument.Parse("""
            # First
            A paragraph
            that continues.

            ## Sub
            - one **bold** item
            - two

            # Second
            Text with an odd ** mark.
            """);

        Assert.Equal(new[] { "First", "Second" }, topics.Select(t => t.Title));
        var blocks = topics[0].Blocks;
        Assert.Equal(new[] { HelpBlockKind.Paragraph, HelpBlockKind.Heading, HelpBlockKind.Bullet, HelpBlockKind.Bullet },
            blocks.Select(b => b.Kind));
        Assert.Equal("A paragraph that continues.", blocks[0].PlainText);
        Assert.Equal(new[] { new HelpSpan("one ", false), new HelpSpan("bold", true), new HelpSpan(" item", false) }, blocks[2].Spans);
        Assert.Equal("Text with an odd ** mark.", Assert.Single(topics[1].Blocks).PlainText);
    }

    [Fact]
    public void Both_languages_have_the_same_topics_and_no_empty_one()
    {
        var ru = HelpDocument.Load("ru");
        var en = HelpDocument.Load("en");

        Assert.True(ru.Count >= 8);
        Assert.Equal(ru.Count, en.Count);
        Assert.All(ru.Concat(en), t => Assert.NotEmpty(t.Blocks));
        // The same structure: each topic has as many subheadings in both.
        Assert.Equal(ru.Select(t => t.Blocks.Count(b => b.IsHeading)), en.Select(t => t.Blocks.Count(b => b.IsHeading)));
        // No bold mark left unpaired (it would show as «**»).
        Assert.All(ru.Concat(en).SelectMany(t => t.Blocks), b => Assert.DoesNotContain("**", b.PlainText));
    }

    [Fact]
    public void The_search_filters_the_topics_and_keeps_a_selection()
    {
        var vm = new HelpViewModel(HelpDocument.Parse("# Holds\nClick a hold.\n# Export\nPress Ctrl+E.\n# Sound\nThe mixer."));
        Assert.Equal("Holds", vm.SelectedTopic?.Title);

        vm.SearchText = "ctrl+e";
        Assert.Equal("Export", Assert.Single(vm.Topics).Title);
        Assert.Equal("Export", vm.SelectedTopic?.Title);

        vm.SearchText = "nothing like this";
        Assert.Empty(vm.Topics);
        Assert.True(vm.NothingFound);
        Assert.Null(vm.SelectedTopic);

        vm.SearchText = "";
        Assert.Equal(3, vm.Topics.Count);
        Assert.False(vm.NothingFound);

        vm.Show("Sound");
        Assert.Equal("Sound", vm.SelectedTopic?.Title);
    }
}
