using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Clippit;
using Clippit.Word;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Publishing;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using static EasyDocs.Api.Tests.Fixtures.DocxFixtures;

// The merge against REAL editor output. Collabora saves through LibreOffice's "MS Word 2007 XML"
// export, which rewrites every paragraph (styles, empty rPr, bookmark ids, numbering ids, header parts)
// even when nobody touched it. A fold that compares raw markup reads all of that as edits and turns
// main's genuine edit into a "conflict" it resolves the wrong way. These run each side through soffice,
// so they skip where it is not installed (like PdfRenderTests); CI installs LibreOffice and runs them.
[Collection(SofficeCollection.Name)]
public class ThreeWayMergeLibreOfficeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static bool SofficeAvailable() => Soffice.Resolve() is not null;

    // Save `docx` the way Collabora does. One retry when soffice produced nothing (a first start on a
    // fresh profile can exit early — the old in-app renderer retried once for the same reason), and a
    // failure that says what soffice said rather than "file not found".
    private static byte[] Lo(byte[] docx)
    {
        var failures = new List<string>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (bytes, failure) = Convert(docx);
            if (bytes is not null) return bytes;
            failures.Add($"attempt {attempt}: {failure}");
        }
        throw new InvalidOperationException("soffice did not convert the document.\n" + string.Join("\n", failures));
    }

    private static (byte[]? Bytes, string Failure) Convert(byte[] docx)
    {
        var work = Directory.CreateTempSubdirectory("edmerge").FullName;
        try
        {
            var src = Path.Combine(work, "in.docx");
            File.WriteAllBytes(src, docx);
            var outDir = Path.Combine(work, "out");
            var psi = new ProcessStartInfo(Soffice.Resolve()!)
            {
                // Its own profile per call, so no two soffice processes ever share one.
                ArgumentList =
                {
                    "-env:UserInstallation=" + new Uri(Path.Combine(work, "profile")).AbsoluteUri,
                    "--headless", "--convert-to", "docx:MS Word 2007 XML", "--outdir", outDir, src,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["HOME"] = work },
            };
            using var p = Process.Start(psi)!;
            // Drain both pipes without blocking, so a hung soffice times out instead of hanging the run.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(entireProcessTree: true);
                return (null, "timed out after 120 s");
            }
            p.WaitForExit(); // flushes the redirected streams
            var output = Path.Combine(outDir, "in.docx");
            if (p.ExitCode == 0 && File.Exists(output)) return (File.ReadAllBytes(output), "");
            return (null, $"exit {p.ExitCode}, output {(File.Exists(output) ? "present" : "missing")}; "
                          + $"stdout: {stdout.Result.Trim()}; stderr: {stderr.Result.Trim()}");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static string[] Paragraphs(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return [.. XDocument.Load(s).Root!.Element(W + "body")!.Descendants(W + "p")
            .Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))];
    }

    // Text of every tracked insertion and deletion.
    private static string Tracked(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return string.Concat(XDocument.Load(s).Descendants().Where(e => e.Name == W + "ins" || e.Name == W + "del")
            .SelectMany(e => e.Descendants().Where(t => t.Name == W + "t" || t.Name == W + "delText")).Select(t => t.Value));
    }

    private static void AssertMerged(byte[] ancestor, byte[] main, byte[] incoming,
        string mainEdit, string incomingEdit, string[] expected)
    {
        var r = WmlComparerMergeService.Merge(ancestor, main, incoming, "Bob");
        var tracked = Tracked(r.Docx);
        // Tracked text is word-level, so these are the distinctive WORDS each side added.
        Assert.DoesNotContain(mainEdit, tracked);  // main's edit is clean base, not a proposed reversion
        Assert.Contains(incomingEdit, tracked);    // the incoming change is tracked
        Assert.Empty(r.Overlaps);                  // nothing both sides touched
        var accepted = RevisionProcessor.AcceptRevisions(new WmlDocument("m.docx", r.Docx)).DocumentByteArray;
        Assert.Equal(expected, Paragraphs(accepted).Where(p => p.Length > 0));
    }

    [SkippableFact]
    public void Main_edit_survives_when_both_sides_were_saved_by_the_editor()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        var ancestor = Build("Alpha", "Bravo", "Charlie");
        AssertMerged(ancestor,
            Lo(Build("Alpha AMENDED", "Bravo", "Charlie")),
            Lo(Build("Alpha", "Bravo", "Charlie EDITED")),
            "AMENDED", "EDITED", ["Alpha AMENDED", "Bravo", "Charlie EDITED"]);
    }

    [SkippableFact]
    public void Main_edit_survives_when_only_the_incoming_side_was_saved_by_the_editor()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        var ancestor = Build("Alpha", "Bravo", "Charlie");
        AssertMerged(ancestor,
            Build("Alpha AMENDED", "Bravo", "Charlie"),
            Lo(Build("Alpha", "Bravo", "Charlie EDITED")),
            "AMENDED", "EDITED", ["Alpha AMENDED", "Bravo", "Charlie EDITED"]);
    }

    // A header and blank lines: LibreOffice writes extra header parts and every blank line is a
    // repeated paragraph — both used to refuse the merge outright.
    [SkippableFact]
    public void A_document_with_a_header_and_blank_lines_merges_after_editor_saves()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        byte[] Doc(string rent, string repairs) =>
            Rich("Lease — page header", P(rent), P(""), P("Deposit is one month."), P(""), P(repairs));
        var ancestor = Doc("The tenant pays rent monthly.", "Repairs by landlord.");
        AssertMerged(ancestor,
            Lo(Doc("The tenant pays rent quarterly.", "Repairs by landlord.")),
            Lo(Doc("The tenant pays rent monthly.", "Repairs by the landlord.")),
            "quarterly", "the", ["The tenant pays rent quarterly.", "Deposit is one month.", "Repairs by the landlord."]);
    }

    // Pandoc (docassemble's markdown attachments) writes bookmarks between paragraphs; LibreOffice
    // moves them inside on save. That used to read as both sides deleting a block, and refused.
    [SkippableFact]
    public void A_pandoc_style_document_with_body_level_bookmarks_merges_after_editor_saves()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        byte[] Doc(string a, string d) =>
            Rich(BookmarkStart("intro", 0), P(a), BookmarkEnd(0), P("Bravo"), P("Charlie"), BookmarkStart("end", 1), P(d), BookmarkEnd(1));
        AssertMerged(Doc("Alpha", "Delta"),
            Lo(Doc("Alpha AMENDED", "Delta")),
            Lo(Doc("Alpha", "Delta EDITED")),
            "AMENDED", "EDITED", ["Alpha AMENDED", "Bravo", "Charlie", "Delta EDITED"]);
    }

    // Layout outside the compared set, three-way, through the full merge and Accept All: whichever side
    // changed a paragraph's indent keeps it, even when the other side reworded that paragraph.
    [SkippableTheory]
    [InlineData(false, "1440")] // incoming rewords AND indents; main edits elsewhere -> incoming's indent
    [InlineData(true, "720")]   // main indents; incoming rewords -> main's indent
    public void An_indent_survives_whichever_side_made_it_after_editor_saves(bool mainIndents, string expectedLeft)
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        var ancestor = Rich(P("Late fees are due."), P("Other clause."));
        var main = mainIndents
            ? Rich(Indented("Late fees are due.", 720), P("Other clause."))
            : Rich(P("Late fees are due."), P("Other clause, amended."));
        var incoming = mainIndents
            ? Rich(P("Late charges are due."), P("Other clause."))
            : Rich(Indented("Late charges are due.", 1440), P("Other clause."));

        var r = WmlComparerMergeService.Merge(ancestor, Lo(main), Lo(incoming), "Bob");
        var accepted = RevisionProcessor.AcceptRevisions(new WmlDocument("m.docx", r.Docx)).DocumentByteArray;
        using var zip = new ZipArchive(new MemoryStream(accepted), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        var late = XDocument.Load(s).Descendants(W + "p")
            .First(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)).StartsWith("Late"));
        Assert.Equal("Late charges are due.", string.Concat(late.Descendants(W + "t").Select(t => t.Value)));
        var ind = late.Element(W + "pPr")?.Element(W + "ind");
        Assert.Equal(expectedLeft, (string?)(ind?.Attribute(W + "left") ?? ind?.Attribute(W + "start")));
    }

    // The same with the ancestor itself an editor save (the steady state once a document has been
    // edited in the browser once).
    [SkippableFact]
    public void Steady_state_editor_saves_on_all_three_sides_merge()
    {
        Skip.IfNot(SofficeAvailable(), "soffice not installed on this host");
        byte[] Doc(string rent, string repairs) =>
            Rich("Lease — page header", P(rent), P(""), P("Deposit is one month."), P(""), P(repairs));
        AssertMerged(Lo(Doc("The tenant pays rent monthly.", "Repairs by landlord.")),
            Lo(Doc("The tenant pays rent quarterly.", "Repairs by landlord.")),
            Lo(Doc("The tenant pays rent monthly.", "Repairs by the landlord.")),
            "quarterly", "the", ["The tenant pays rent quarterly.", "Deposit is one month.", "Repairs by the landlord."]);
    }
}
