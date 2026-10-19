using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AiVideoEditor.UI.Tests.Localization;

/// <summary>
/// D039 §5: guards against untranslated user-visible text. They read the sources, so they also catch text no other test
/// reaches: (1) AXAML attributes that show text are bindings or resources, never English literals; (2) both resource pairs
/// have the same keys, no empty value, the same placeholders and valid plural forms, and the Russian is Russian; (3) string
/// literals of two or more Latin words in <c>src/</c> are either logging / internal exceptions or listed, with a reason, in
/// <c>untranslated-allowlist.txt</c> — and every listed literal still exists, so the list only shrinks.
/// </summary>
public sealed partial class LocalizationGuardTests
{
    private static readonly string Root = FindRoot();

    // Attributes whose value is shown to the user.
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "ToolTip.Tip", "Title", "Watermark", "Header", "PlaceholderText"
    };

    // Literals allowed in AXAML: symbols have no Latin letter and need no entry; these are the technical tokens.
    private static readonly HashSet<string> AxamlAllowed = new(StringComparer.Ordinal)
    {
        "M"   // the mute button of a track header (as in other editors; its tooltip is translated)
    };

    [Fact]
    public void Axaml_shows_no_English_literal()
    {
        var found = new HashSet<(string File, string Literal)>();
        foreach (var file in SourceFiles("*.axaml"))
        {
            var rel = Rel(file);
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var value = attribute.Value;
                    if (value.StartsWith('{'))
                    {
                        var format = StringFormat().Match(value);
                        if (format.Success && HasLatin(format.Groups[1].Value)) found.Add((rel, format.Groups[1].Value));
                        continue;
                    }
                    if (TextAttributes.Contains(attribute.Name.LocalName) && HasLatin(value) && !AxamlAllowed.Contains(value))
                        found.Add((rel, value));
                }
                if (!element.HasElements && element.Name.LocalName is "TextBlock" or "Run" or "Button" && HasLatin(element.Value))
                    found.Add((rel, element.Value));
            }
        }
        AssertListed(found, ".axaml");
    }

    [Theory]
    [InlineData("src/Core/Localization/Messages")]
    [InlineData("src/UI/Localization/Strings")]
    public void Resources_are_complete_and_Russian(string basePath)
    {
        var en = ReadResx(Path.Combine(Root, basePath + ".resx"));
        var ru = ReadResx(Path.Combine(Root, basePath + ".ru.resx"));
        var problems = new List<string>();

        foreach (var key in en.Keys.Except(ru.Keys)) problems.Add($"{key}: no Russian text");
        foreach (var key in ru.Keys.Except(en.Keys)) problems.Add($"{key}: Russian text without an English one");
        foreach (var (key, english) in en)
        {
            if (!ru.TryGetValue(key, out var russian)) continue;
            if (string.IsNullOrWhiteSpace(english)) problems.Add($"{key}: empty English text");
            if (string.IsNullOrWhiteSpace(russian)) { problems.Add($"{key}: empty Russian text"); continue; }
            if (!Placeholders(english).SetEquals(Placeholders(russian)))
                problems.Add($"{key}: placeholders differ ('{english}' / '{russian}')");
            var enForms = english.Split('|').Length;
            var ruForms = russian.Split('|').Length;
            if ((enForms, ruForms) is not ((1, 1) or (2, 3)))
                problems.Add($"{key}: plural forms {enForms} / {ruForms} (expected 2 / 3)");
            if (HasLatinWord(english) && !Cyrillic().IsMatch(russian) && !SameInBothLanguages.Contains(key))
                problems.Add($"{key}: the Russian text has no Russian ('{russian}')");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // Keys whose text is a technical token in both languages.
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void Source_literals_are_translated_or_listed()
    {
        var found = new HashSet<(string File, string Literal)>();
        foreach (var file in SourceFiles("*.cs"))
        {
            if (file.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
            var rel = Rel(file);
            foreach (var line in File.ReadLines(file))
            {
                if (SkippedLine().IsMatch(line)) continue;
                foreach (Match m in Literal().Matches(line))
                    if (LatinWords().IsMatch(Holes().Replace(m.Groups[1].Value, " "))) found.Add((rel, m.Groups[1].Value.TrimEnd()));
            }
        }
        AssertListed(found, ".cs");
    }

    /// <summary>Every found literal is listed and every listed literal (of these files) is still found. The message lists
    /// the differences in the allowlist's own format.</summary>
    private static void AssertListed(HashSet<(string File, string Literal)> found, string extension)
    {
        var allowFile = Path.Combine(Root, "tests", "UI.Tests", "Localization", "untranslated-allowlist.txt");
        var allowed = File.ReadLines(allowFile)
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', 2))
            .Select(p => (File: p[0], Literal: p.Length > 1 ? p[1].TrimEnd() : ""))   // stored without trailing spaces (git diff --check)
            .Where(a => a.File.EndsWith(extension, StringComparison.Ordinal))
            .ToHashSet();
        var unlisted = found.Except(allowed).OrderBy(a => a.File, StringComparer.Ordinal).Select(a => $"{a.File}\t{a.Literal}").ToList();
        var stale = allowed.Except(found).OrderBy(a => a.File, StringComparer.Ordinal).Select(a => $"{a.File}\t{a.Literal}").ToList();
        Assert.True(unlisted.Count == 0 && stale.Count == 0,
            $"Untranslated text ({unlisted.Count}) — move it to a resource, or list a technical one with a reason:\n"
            + string.Join("\n", unlisted) + $"\nListed but gone ({stale.Count}) — remove from the allowlist:\n"
            + string.Join("\n", stale));
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static HashSet<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    private static bool HasLatin(string text) => text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    private static bool HasLatinWord(string text) => LatinWord().IsMatch(text);
    private static string Rel(string file) => Path.GetRelativePath(Root, file).Replace('\\', '/');

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AiVideoEditor.sln"))) return dir.FullName;
        throw new InvalidOperationException("The repository root (AiVideoEditor.sln) was not found.");
    }

    [GeneratedRegex(@"StringFormat='([^']*)'")] private static partial Regex StringFormat();
    [GeneratedRegex(@"\{(\d+)[^}]*\}")] private static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"[Ѐ-ӿ]")] private static partial Regex Cyrillic();
    [GeneratedRegex(@"[A-Za-z]{2,}")] private static partial Regex LatinWord();
    // A C# string literal on one line (regular, interpolated or verbatim); its text is group 1.
    [GeneratedRegex(@"\$?@?""((?:[^""\\\n]|\\.)*)""")] private static partial Regex Literal();
    // Two Latin words in a row: a sentence, not an identifier, a path or a format token.
    [GeneratedRegex(@"[A-Za-z][A-Za-z']+[,.:;!?]?\s+[A-Za-z]{2,}")] private static partial Regex LatinWords();
    // Interpolation holes and format items are not text.
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex Holes();
    // Comments, attributes, logging, internal exceptions, undo descriptions and debug checks are not shown to the user.
    [GeneratedRegex(@"^\s*(//|\*|\[)|\b(_?[lL]ogger|Log)\.\w+\(|\bLog(Information|Warning|Error|Debug|Trace|Critical)\(|throw new (InvalidOperation|Argument|ArgumentOutOfRange|ArgumentNull|NotSupported|NotImplemented|ObjectDisposed|Format|InvalidData|Unreachable)Exception|Description\s*=>|Debug\.(Assert|Fail)")]
    private static partial Regex SkippedLine();
}
