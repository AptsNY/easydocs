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

    // Not a zip => WmlComparer must degrade, never throw.
    public static byte[] Malformed() => new byte[] { 1, 2, 3 };

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
