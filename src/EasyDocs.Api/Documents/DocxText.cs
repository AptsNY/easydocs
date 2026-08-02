using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EasyDocs.Api.Documents;

// Plain-text extraction for the content index (issue #12): unzip word/document.xml and take the
// text nodes. No dependency — ZipArchive + XmlReader are the whole parser. Anything that is not a
// docx (a PDF version, a legacy .doc) extracts to "", which is also how a stale index entry gets
// cleared when a document's head stops being a docx.
public static class DocxText
{
    // Postgres tsvectors cap at 1MB; half that in characters keeps the row comfortably under it.
    public const int MaxChars = 500_000;

    public static string Extract(Stream docx)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return ""; } // not a zip — PDF or .doc version
        using (zip)
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return ""; // a zip, but not a docx

            var sb = new StringBuilder();
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, // the entry is untrusted member input
                XmlResolver = null,
            });
            var lastWasBreak = true;
            while (sb.Length < MaxChars && reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
                {
                    sb.Append(reader.Value);
                    lastWasBreak = false;
                }
                // Word encodes structure, not whitespace: without these, "one paragraph" and the
                // next would concatenate into one searchable pseudo-word.
                else if (reader.NodeType == XmlNodeType.Element
                         && reader.LocalName is "p" or "br" or "tab" or "cr" && !lastWasBreak)
                {
                    sb.Append(' ');
                    lastWasBreak = true;
                }
            }
            return sb.Length <= MaxChars ? sb.ToString().Trim() : sb.ToString(0, MaxChars).Trim();
        }
    }
}
