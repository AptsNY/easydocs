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

    private static byte[] Build(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new W.Body();
            foreach (var p in paragraphs)
                body.Append(new W.Paragraph(new W.Run(new W.Text(p))));
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
