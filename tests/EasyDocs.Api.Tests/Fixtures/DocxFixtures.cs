using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace EasyDocs.Api.Tests.Fixtures;

// Real (minimal) OOXML so Clippit.WmlComparer has something to compare — the 5-byte fakes M0 used
// won't parse. A styles part is required: WmlComparer touches the styles/footnotes parts.
public static class DocxFixtures
{
    public static byte[] Base() => Build("Alpha", "Bravo", "Charlie");

    // "Bravo" -> "Bravo EDITED" and a new "Delta" paragraph => a compare yields non-zero insertions.
    public static byte[] Edited() => Build("Alpha", "Bravo EDITED", "Charlie", "Delta");

    // The incoming concurrent-branch head: main's content (== Edited) PLUS one distinctive edit ("Echo").
    // A merge-into-main redline of this over the Edited main head is purely "Echo" as a tracked change,
    // leaving the first author's edits as clean (untracked) base (Task 9 merge-into-main test).
    public static byte[] EditedPlusEcho() => Build("Alpha", "Bravo EDITED", "Charlie", "Delta", "Echo");

    // Not a zip => WmlComparer must degrade, never throw.
    public static byte[] Malformed() => new byte[] { 1, 2, 3 };

    // Blobs are content-addressed and version_diffs is keyed by (from_sha, to_sha), so EVERY test that
    // compares Base() to Edited() shares one row. That is fine while they only read it, and a trap for a
    // test that needs to own the row — it would be racing every other diff test in the assembly. This
    // mints a pair no other test can collide with.
    public static (byte[] From, byte[] To) UniquePair()
    {
        var marker = Guid.NewGuid().ToString("N");
        return (Build("Alpha", marker, "Charlie"),
                Build("Alpha", marker + " EDITED", "Charlie", "Delta " + marker));
    }

    public static byte[] Build(params string[] paragraphs)
        => Package(paragraphs.Select(p => new W.Paragraph(new W.Run(new W.Text(p)))).ToArray());

    // One paragraph with a real <w:tab/> mid-run — a tab STOP, not a tab character in a text node.
    public static byte[] WithTab(string before, string after)
        => Package(new W.Paragraph(new W.Run(new W.Text(before), new W.TabChar(), new W.Text(after))));

    // "The tenant pays rent <ins>quarterly</ins><del>monthly</del>." followed by a PAGE field — what a
    // merge result (or any tracked-changes save) looks like. Deleted text and field instructions are
    // text nodes too, but not the document's current content.
    public static byte[] WithTrackedChange() => Package(new W.Paragraph(
        new W.Run(new W.Text("The tenant pays rent ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
        new W.InsertedRun(new W.Run(new W.Text("quarterly"))) { Id = "1", Author = "A" },
        new W.DeletedRun(new W.Run(new W.DeletedText("monthly"))) { Id = "2", Author = "A" },
        new W.Run(new W.Text(".")),
        new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
        new W.Run(new W.FieldCode(" PAGE ")),
        new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })));

    // Richer blocks for the merge fold's refusal paths: each builds its element against the package it
    // lands in, so relationships (links, images) and notes are real.
    public delegate DocumentFormat.OpenXml.OpenXmlElement Block(MainDocumentPart part);

    public static Block P(string text) => _ => new W.Paragraph(new W.Run(new W.Text(text)));

    public static Block Link(string text, string url, string relId) => part =>
    {
        part.AddHyperlinkRelationship(new Uri(url), true, relId);
        return new W.Paragraph(new W.Hyperlink(new W.Run(new W.Text(text))) { Id = relId });
    };

    // A caption plus a w:drawing whose blip embeds `png` (not a renderable drawing — the fold only
    // needs the relationship).
    public static Block Image(string caption, byte[] png) => part =>
    {
        var img = part.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(png)) img.FeedData(s);
        return new W.Paragraph(new W.Run(new W.Text(caption)),
            new W.Run(new W.Drawing(new DocumentFormat.OpenXml.Drawing.Blip { Embed = part.GetIdOfPart(img) })));
    };

    public static Block Table(params string[][] rows) => _ =>
        new W.Table(rows.Select(r => new W.TableRow(r.Select(c => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(c))))))));

    // A table with an explicit column grid (twips per column).
    public static Block GridTable(int[] widths, params string[][] rows) => _ =>
        new W.Table([new W.TableGrid(widths.Select(w => new W.GridColumn { Width = w.ToString() })),
            .. rows.Select(r => new W.TableRow(r.Select(c => new W.TableCell(new W.Paragraph(new W.Run(new W.Text(c)))))))]);

    // A paragraph with a left indent (twips) — formatting the merge's meaning comparison does not see.
    public static Block Indented(string text, int left) => _ =>
        new W.Paragraph(new W.ParagraphProperties(new W.Indentation { Left = left.ToString() }), new W.Run(new W.Text(text)));

    // Body-level range markers, as pandoc writes them: a bookmark opened and closed BETWEEN
    // paragraphs rather than inside one.
    public static Block BookmarkStart(string name, int id) => _ => new W.BookmarkStart { Name = name, Id = id.ToString() };
    public static Block BookmarkEnd(int id) => _ => new W.BookmarkEnd { Id = id.ToString() };

    // The same bookmark as LibreOffice writes it after a save: inside the paragraph.
    public static Block Bookmarked(string text, string name, int id) => _ =>
        new W.Paragraph(new W.BookmarkStart { Name = name, Id = id.ToString() }, new W.Run(new W.Text(text)),
            new W.BookmarkEnd { Id = id.ToString() });

    // A paragraph referencing footnote 1, whose text is `note`.
    public static Block Footnoted(string text, string note) => part =>
    {
        var fp = part.FootnotesPart ?? part.AddNewPart<FootnotesPart>();
        fp.Footnotes = new W.Footnotes(new W.Footnote(new W.Paragraph(new W.Run(new W.Text(note)))) { Id = 1 });
        fp.Footnotes.Save();
        return new W.Paragraph(new W.Run(new W.Text(text)), new W.Run(new W.FootnoteReference { Id = 1 }));
    };

    public static byte[] Rich(params Block[] blocks) => Rich(null, blocks);

    public static byte[] Rich(string? header, params Block[] blocks) => Rich(header, null, blocks);

    // `headerImage`: a logo in the header (a drawing whose blip the header part embeds).
    public static byte[] Rich(string? header, byte[]? headerImage, params Block[] blocks)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            main.Document = new W.Document(body);
            foreach (var b in blocks) body.Append(b(main));
            if (header is not null)
            {
                var hp = main.AddNewPart<HeaderPart>();
                hp.Header = new W.Header(new W.Paragraph(new W.Run(new W.Text(header))));
                if (headerImage is not null)
                {
                    var img = hp.AddImagePart(ImagePartType.Png);
                    using (var s = new MemoryStream(headerImage)) img.FeedData(s);
                    hp.Header.Append(new W.Paragraph(new W.Run(new W.Drawing(
                        new DocumentFormat.OpenXml.Drawing.Blip { Embed = hp.GetIdOfPart(img) }))));
                }
                hp.Header.Save();
                body.Append(new W.SectionProperties(new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(hp) }));
            }
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle()),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle())));
            styles.Styles.Save();
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] Package(params W.Paragraph[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            foreach (var p in paragraphs)
                body.Append(p);
            main.Document = new W.Document(body);

            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle()),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle())));
            styles.Styles.Save();
            main.Document.Save();
        }
        return ms.ToArray();
    }
}
