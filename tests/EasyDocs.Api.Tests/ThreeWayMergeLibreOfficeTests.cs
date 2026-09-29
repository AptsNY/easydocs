using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Clippit;
using Clippit.Word;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Publishing;
using EasyDocs.Api.Tests.Fixtures;
using static EasyDocs.Api.Tests.Fixtures.DocxFixtures;

// The merge against REAL editor output. Collabora saves through LibreOffice's "MS Word 2007 XML"
// export, which rewrites every paragraph (styles, empty rPr, bookmark ids, numbering ids, header parts)
// even when nobody touched it. A fold that compares raw markup reads all of that as edits and turns
// main's genuine edit into a "conflict" it resolves the wrong way. These run each side through soffice,
// so they skip where it is not installed (like PdfRenderTests); CI installs LibreOffice and runs them.
public class ThreeWayMergeLibreOfficeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static bool SofficeAvailable() => LibreOfficePdfRenderer.ResolveSoffice() is not null;

    // Save `docx` the way Collabora does.
    private static byte[] Lo(byte[] docx)
    {
        var work = Directory.CreateTempSubdirectory("edmerge").FullName;
        try
        {
            var src = Path.Combine(work, "in.docx");
            File.WriteAllBytes(src, docx);
            var outDir = Path.Combine(work, "out");
            var psi = new ProcessStartInfo(LibreOfficePdfRenderer.ResolveSoffice()!)
            {
                ArgumentList = { "--headless", "--convert-to", "docx:MS Word 2007 XML", "--outdir", outDir, src },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["HOME"] = work },
            };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            return File.ReadAllBytes(Path.Combine(outDir, "in.docx"));
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
