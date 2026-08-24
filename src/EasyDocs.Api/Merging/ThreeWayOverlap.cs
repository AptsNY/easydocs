using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Clippit.Word;

namespace EasyDocs.Api.Merging;

/// <summary>
/// Pure: given two documents each compared against the SAME base, report the base paragraphs that both
/// comparisons changed. No database, no blob store, no host — the fiddly part of the three-way review
/// is testable on its own (spec: 2026-08-24-three-way-merge-review-design.md).
/// </summary>
/// <remarks>
/// ponytail: a paragraph SPLIT on one side (someone pressing Enter mid-paragraph) shifts that side's
/// ordinals by one from the split down, so overlaps after it can be missed or attributed to the
/// neighbouring paragraph. This is why the result ships worded as a hint and never blocks a merge.
/// Upgrade path: anchor on w14:paraId where the attribute is present, falling back to the ordinal.
///
/// If you take that upgrade path: do NOT reach for pt14:Unid. Clippit stamps every w:p with one and it
/// looks like the perfect anchor, but the values are regenerated per comparison run — the same base
/// paragraph gets a different Unid in Compare(base, main) than in Compare(base, incoming), so matching
/// on it silently finds nothing. w14:paraId is a different attribute, author-stamped and carried from
/// the source document, and is the right one. (Likewise unusable: w:author is always
/// "Open-Xml-PowerTools" and w:id restarts at 0 per comparison.)
/// </remarks>
public static class ThreeWayOverlap
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public record Paragraph(int Ordinal, string Text);

    public static IReadOnlyList<Paragraph> Find(WmlDocument comparedMain, WmlDocument comparedIncoming)
    {
        var main = Touched(comparedMain);
        var incoming = Touched(comparedIncoming);

        // Labels are taken from either side: both reconstruct the SAME base text for a given ordinal,
        // because both were compared against the same base.
        return main.Keys.Where(incoming.ContainsKey).Order()
            .Select(o => new Paragraph(o, main[o]))
            .ToList();
    }

    // base-paragraph ordinal -> its label, for the base paragraphs this comparison changed.
    private static Dictionary<int, string> Touched(WmlDocument compared)
    {
        using var zip = new ZipArchive(new MemoryStream(compared.DocumentByteArray), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("compared docx has no word/document.xml");
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var touched = new Dictionary<int, string>();
        var ordinal = 0;
        foreach (var p in doc.Descendants(W + "p"))
        {
            var hasBaseContent = p.Descendants(W + "t").Any(t => !t.Ancestors(W + "ins").Any())
                              || p.Descendants(W + "delText").Any();
            var hasInsertion = p.Descendants(W + "ins").Any();

            // A paragraph that is ALL insertion never existed in the base, so numbering it would shift
            // every later ordinal out of step with the other comparison — which is exactly the bug this
            // anchor exists to avoid. Skipping them is what makes `ordinal` the BASE document's own
            // paragraph numbering, identical across both comparisons by construction.
            //
            // The test is "has insertions AND no base content" rather than "has no base content", so a
            // genuinely EMPTY base paragraph (common in real documents) is still counted.
            if (hasInsertion && !hasBaseContent) continue;

            if (hasInsertion || p.Descendants(W + "del").Any())
                touched[ordinal] = Label(BaseText(p));
            ordinal++;
        }
        return touched;
    }

    // The paragraph as it stood in the base: text that survived (w:t outside w:ins) plus text that was
    // removed (w:delText), in document order. Insertions are excluded — they were never in the base, so
    // including them would label a paragraph with words one author has just added.
    private static string BaseText(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var t in p.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "delText"))
            if (t.Name == W + "delText" || !t.Ancestors(W + "ins").Any())
                sb.Append(t.Value);
        return sb.ToString().Trim();
    }

    private const int LabelLength = 60;

    private static string Label(string text) =>
        text.Length <= LabelLength ? text : text[..LabelLength].TrimEnd() + "…";
}
