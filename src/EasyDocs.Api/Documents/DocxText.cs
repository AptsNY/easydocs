using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EasyDocs.Api.Documents;

// Plain-text extraction for the content index (issue #12) and the /text endpoint: unzip
// word/document.xml and take the text nodes. No dependency — ZipArchive + XmlReader are the whole
// parser.
public static class DocxText
{
    // Postgres tsvectors cap at 1MB; half that in characters keeps the row comfortably under it.
    public const int MaxChars = 500_000;

    /// <summary>
    /// A null <c>Text</c> means these bytes are not a docx at all — not a zip, or a zip with no
    /// <c>word/document.xml</c>. An empty <c>Text</c> means a real docx with nothing in it. Callers
    /// serving a reader must tell those apart, and sniffing cannot: <c>BlobMime.Sniff</c> defaults to
    /// docx. The index is happy to treat both as nothing to index.
    /// </summary>
    public static (string? Text, bool Truncated) Extract(Stream docx)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return (null, false); } // not a zip — PDF or .doc version
        using (zip)
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return (null, false); // a zip, but not a docx

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
                // next would concatenate into one searchable pseudo-word. A newline rather than a
                // space because a reader gets paragraphs out of it and the index does not care —
                // SearchVector is a stored to_tsvector('simple', "Text") column, and both characters
                // are `blank` to the parser: same tokens, same positions, same phrase queries.
                else if (reader.NodeType == XmlNodeType.Element
                         && reader.LocalName is "p" or "br" or "tab" or "cr" && !lastWasBreak)
                {
                    // A <w:tab/> is a tab, not the end of a line — only the other three break.
                    sb.Append(reader.LocalName == "tab" ? '\t' : '\n');
                    lastWasBreak = true;
                }
            }

            // The loop's own exit condition, read before Trim eats the boundary character — which is
            // why a caller cannot compute this from the returned string's length.
            var truncated = sb.Length >= MaxChars;
            return (truncated ? sb.ToString(0, MaxChars).Trim() : sb.ToString().Trim(), truncated);
        }
    }
}
