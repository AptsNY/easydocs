using System.IO.Compression;
using System.Text;
using System.Xml;

namespace EasyDocs.Api.Documents;

// Plain-text extraction for the content index (issue #12) and the /text endpoint: unzip
// word/document.xml and take the text nodes. No dependency — ZipArchive + XmlReader are the whole
// parser.
public static class DocxText
{
    /// <summary>
    /// What the bytes turned out to be. <c>Text is null</c> means they are not a docx at all (not a
    /// zip, or a zip with no <c>word/document.xml</c> — a PDF or legacy .doc version); <c>Text is
    /// ""</c> means a real docx with no text in it. The index treats both as nothing to index; a
    /// caller serving a reader must not, and sniffing cannot separate them because
    /// <see cref="Storage.BlobMime.Sniff"/> defaults to docx.
    /// </summary>
    public readonly record struct Extraction(string? Text, bool Truncated);

    // Postgres tsvectors cap at 1MB; half that in characters keeps the row comfortably under it.
    public const int MaxChars = 500_000;

    public static Extraction Extract(Stream docx)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(docx, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { return new Extraction(null, false); } // not a zip — PDF or .doc version
        using (zip)
        {
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null) return new Extraction(null, false); // a zip, but not a docx

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

            // The loop's own exit condition, read before Trim eats the boundary character — which is
            // why a caller cannot compute this from the returned string's length.
            var truncated = sb.Length >= MaxChars;
            return new Extraction(truncated ? sb.ToString(0, MaxChars).Trim() : sb.ToString().Trim(), truncated);
        }
    }
}
