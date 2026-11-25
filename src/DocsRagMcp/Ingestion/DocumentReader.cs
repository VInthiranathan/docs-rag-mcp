using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocsRagMcp.Ingestion;

/// <summary>A piece of a document, with a readable location such as a Markdown heading or a PDF page.</summary>
public sealed record DocumentPart(string Text, string? Location);

/// <summary>Turns Markdown, plain-text and PDF files into text parts that keep track of where they came from.</summary>
public static class DocumentReader
{
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    private static readonly HashSet<string> PlainTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".rst", ".adoc" };

    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        [.. MarkdownExtensions, .. PlainTextExtensions, ".pdf"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DocumentPart> Read(string path)
    {
        var extension = Path.GetExtension(path);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path);
        }

        var text = File.ReadAllText(path);
        return MarkdownExtensions.Contains(extension)
            ? SplitMarkdown(text)
            : string.IsNullOrWhiteSpace(text) ? [] : [new DocumentPart(text.Trim(), null)];
    }

    /// <summary>
    /// Splits Markdown at its headings, so each part knows which section it belongs to.
    /// Lines that look like headings inside code blocks are left alone.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitMarkdown(string markdown)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var contentStart = GetContentStart(lines, out var heading);
        var inCodeBlock = false;

        foreach (var line in lines.Skip(contentStart))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
            }
            else if (!inCodeBlock && TryParseHeading(trimmed, out var title))
            {
                Flush();
                heading = title;
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return parts;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();

            // A heading directly followed by another heading carries no content of its own.
            var isHeadingOnly = !text.Contains('\n') && TryParseHeading(text, out _);
            if (text.Length > 0 && !isHeadingOnly)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    // Only recognize a complete block of simple YAML fields and their lists.
    // Leave unsupported or ambiguous blocks in the document rather than losing text.
    private static int GetContentStart(string[] lines, out string? title)
    {
        title = null;
        var end = lines[0] == "---" ? Array.IndexOf(lines, "---", 1) : -1;
        if (end < 0)
        {
            return 0;
        }

        var fields = lines[1..end].Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (fields.Length == 0 || !IsSimpleMetadata(fields))
        {
            return 0;
        }

        const string titlePrefix = "title:";
        var titleLine = fields.FirstOrDefault(line => line.StartsWith(titlePrefix, StringComparison.Ordinal));
        var value = titleLine?[titlePrefix.Length..].Trim();
        if (value is { Length: >= 2 } && (value[0] is '\'' or '"') && value[0] == value[^1])
        {
            value = value[1..^1];
        }

        title = string.IsNullOrWhiteSpace(value) ? null : value;
        return end + 1;
    }

    private static bool IsSimpleMetadata(string[] fields)
    {
        var allowsList = false;
        foreach (var line in fields)
        {
            if (Regex.IsMatch(line, @"^[A-Za-z_][A-Za-z0-9_.-]*:(?:[ \t]+.*)?$"))
            {
                allowsList = line[(line.IndexOf(':') + 1)..].Trim().Length == 0;
            }
            else if (!allowsList || !line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseHeading(string line, out string title)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is >= 1 and <= 6 && level < line.Length && line[level] == ' ')
        {
            title = line[(level + 1)..].Trim().TrimEnd('#').Trim();
            return title.Length > 0;
        }

        title = string.Empty;
        return false;
    }

    private static IReadOnlyList<DocumentPart> ReadPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var parts = new List<DocumentPart>();

        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length > 0)
            {
                parts.Add(new DocumentPart(text, $"page {page.Number}"));
            }
        }

        return parts;
    }
}
