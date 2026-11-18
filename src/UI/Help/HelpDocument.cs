using System.Globalization;
using System.Reflection;
using System.Text;

namespace AiVideoEditor.UI.Help;

/// <summary>A piece of a help line: plain or bold text (<c>**…**</c> in the source).</summary>
public sealed record HelpSpan(string Text, bool IsBold);

public enum HelpBlockKind { Heading, Paragraph, Bullet }

/// <summary>One block of a topic: a subheading (<c>## …</c>), a paragraph or a bullet (<c>- …</c>).</summary>
public sealed record HelpBlock(HelpBlockKind Kind, IReadOnlyList<HelpSpan> Spans)
{
    public bool IsHeading => Kind == HelpBlockKind.Heading;
    public bool IsParagraph => Kind == HelpBlockKind.Paragraph;
    public bool IsBullet => Kind == HelpBlockKind.Bullet;

    /// <summary>The block's text without the bold marks (search).</summary>
    public string PlainText => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>One topic of the help (<c># …</c> in the source) and its blocks.</summary>
public sealed record HelpTopic(string Title, IReadOnlyList<HelpBlock> Blocks)
{
    /// <summary>Whether the title or any block contains <paramref name="query"/> (case-insensitive).</summary>
    public bool Matches(string query) =>
        Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Blocks.Any(b => b.PlainText.Contains(query, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>
/// D068 §3: the in-app help — a small text format embedded in the UI assembly (<c>Help/help.ru.md</c>, <c>help.en.md</c>),
/// one file per interface language. <c># Title</c> starts a topic, <c>## Title</c> a subheading inside it, <c>- </c> a
/// bullet; other lines are paragraphs (consecutive lines join into one, an empty line ends it); <c>**text**</c> is bold.
/// No Markdown library: only this subset is understood, everything else is shown as text.
/// </summary>
public static class HelpDocument
{
    /// <summary>The topics in the interface's language (<see cref="CultureInfo.CurrentUICulture"/>): Russian for "ru",
    /// English otherwise.</summary>
    public static IReadOnlyList<HelpTopic> LoadForCurrentLanguage() =>
        Load(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en");

    public static IReadOnlyList<HelpTopic> Load(string language)
    {
        var name = $"AiVideoEditor.UI.Help.help.{language}.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The help resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    public static IReadOnlyList<HelpTopic> Parse(string text)
    {
        var topics = new List<HelpTopic>();
        string? title = null;
        var blocks = new List<HelpBlock>();
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new HelpBlock(HelpBlockKind.Paragraph, Spans(paragraph.ToString())));
            paragraph.Clear();
        }

        void FlushTopic()
        {
            FlushParagraph();
            if (title is not null) topics.Add(new HelpTopic(title, blocks.ToList()));
            blocks.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock(HelpBlockKind.Heading, Spans(line[3..].Trim())));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushTopic();
                title = line[2..].Trim();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, Spans(line[2..].Trim())));
            }
            else if (line.Length == 0)
            {
                FlushParagraph();
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
        }
        FlushTopic();
        return topics;
    }

    /// <summary>Splits a line at its <c>**</c> marks; an unpaired mark is kept as text.</summary>
    public static IReadOnlyList<HelpSpan> Spans(string line)
    {
        var parts = line.Split("**");
        if (parts.Length % 2 == 0)                            // an odd number of marks: not bold, as written
            return new[] { new HelpSpan(line, false) };
        var spans = new List<HelpSpan>();
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) spans.Add(new HelpSpan(parts[i], i % 2 == 1));
        return spans;
    }
}
