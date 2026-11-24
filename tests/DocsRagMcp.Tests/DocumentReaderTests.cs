using DocsRagMcp.Ingestion;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocsRagMcp.Tests;

public class DocumentReaderTests
{
    [Fact]
    public void Markdown_is_split_at_headings()
    {
        var parts = DocumentReader.SplitMarkdown("Intro text.\n\n# Install\nRun the installer.\n\n## Windows\nUse winget.\n");

        Assert.Collection(
            parts,
            part =>
            {
                Assert.Null(part.Location);
                Assert.Equal("Intro text.", part.Text);
            },
            part =>
            {
                Assert.Equal("Install", part.Location);
                Assert.Contains("Run the installer.", part.Text);
            },
            part =>
            {
                Assert.Equal("Windows", part.Location);
                Assert.Contains("Use winget.", part.Text);
            });
    }

    [Fact]
    public void Hash_lines_inside_code_blocks_are_not_headings()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("# Setup\n```bash\n# install dependencies\nnpm install\n```\nDone."));

        Assert.Equal("Setup", part.Location);
        Assert.Contains("# install dependencies", part.Text);
    }

    [Fact]
    public void Heading_with_no_text_of_its_own_is_skipped()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("# Guide\n## Part one\nSome text."));

        Assert.Equal("Part one", part.Location);
    }

    [Fact]
    public void Front_matter_title_labels_only_the_intro()
    {
        var parts = DocumentReader.SplitMarkdown("---\ntitle: Travel policy\ntags: [hr, travel]\n---\nIntro.\n---\nMore intro.\n# Expenses\nKeep receipts.");

        Assert.Collection(parts,
            part => Assert.Equal(new DocumentPart("Intro.\n---\nMore intro.", "Travel policy"), part),
            part => Assert.Equal(new DocumentPart("# Expenses\nKeep receipts.", "Expenses"), part));
    }

    [Theory]
    [InlineData("---\ntags: [hr]\n---\nIntro.", null)]
    [InlineData("---\ntags:\n  - hr\n  - travel\n---\nIntro.", null)]
    [InlineData("---\ntags:\n- hr\n- travel\n---\nIntro.", null)]
    [InlineData("---\r\ntitle: Travel policy\r\n---\r\nIntro.", "Travel policy")]
    [InlineData("---\ntitle: \"Travel policy\"\n---\nIntro.", "Travel policy")]
    [InlineData("---\ntitle: 'Travel policy'\n---\nIntro.", "Travel policy")]
    [InlineData("---\ntitle:\n---\nIntro.", null)]
    public void Simple_front_matter_is_skipped(string markdown, string? title)
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown(markdown));
        Assert.Equal(new DocumentPart("Intro.", title), part);
    }

    [Theory]
    [InlineData("---\nImportant information.\n---\nKeep receipts.")]
    [InlineData("---\ntitle: Example\nImportant information.\n---\nKeep receipts.")]
    [InlineData("---\ntitle: Example\nBody")]
    [InlineData("---\n---\nBody")]
    [InlineData("Before\n---\ntitle: Example\n---\nAfter")]
    [InlineData("```yaml\n---\ntitle: Example\n---\n```")]
    [InlineData("---\ntags: example\n- ordinary text\n---\nBody")]
    [InlineData("---\n- ordinary text\n---\nBody")]
    [InlineData("---\nauthor:\n  name: Example\n---\nBody")]
    [InlineData("---\nhttps://example.com\n---\nBody")]
    [InlineData("\n---\ntitle: Example\n---\nBody")]
    public void Ambiguous_or_unsupported_blocks_are_preserved(string markdown)
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown(markdown));
        Assert.Equal(new DocumentPart(markdown.Trim(), null), part);
    }

    [Fact]
    public void Front_matter_without_body_produces_no_parts()
    {
        Assert.Empty(DocumentReader.SplitMarkdown("---\ntitle: Example\n---"));
    }

    [Fact]
    public void First_heading_overrides_front_matter_title()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("---\ntitle: Guide\n---\n# Setup\nInstall."));
        Assert.Equal(new DocumentPart("# Setup\nInstall.", "Setup"), part);
    }

    [Fact]
    public void Pdf_is_read_page_by_page()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("policy.pdf");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Invoices are due in 30 days.", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PageSize.A4).AddText("Refunds take five business days.", 12, new PdfPoint(25, 700), font);
        File.WriteAllBytes(path, builder.Build());

        var parts = DocumentReader.Read(path);

        Assert.Equal(["page 1", "page 2"], parts.Select(part => part.Location));
        Assert.Contains("Refunds take five business days.", parts[1].Text);
    }

    [Theory]
    [InlineData("guide.md", true)]
    [InlineData("REPORT.PDF", true)]
    [InlineData("notes.txt", true)]
    [InlineData("Program.cs", false)]
    [InlineData("contract.docx", false)]
    public void Supported_file_types(string fileName, bool supported)
    {
        Assert.Equal(supported, DocumentReader.IsSupported(fileName));
    }
}
