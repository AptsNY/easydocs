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
            // A paragraph that never existed in the base must not be numbered, or every ordinal below it
            // shifts out of step with the other comparison — the precise failure this anchor exists to
            // prevent. WmlComparer marks a NEW paragraph by inserting its paragraph MARK
            // (w:pPr/w:rPr/w:ins), so that mark is the direct evidence, not an inference.
            //
            // It replaced one: "has insertions and no surviving base text". That read as equivalent and
            // is not. An empty base paragraph — a spacer, a blank line under a heading — that an author
            // types into has insertions and no surviving base text, yet it DID exist in the base. Skipping
            // it desynced everything below, and the visible symptom was the worst kind: the hint naming a
            // clause neither author had touched, in the one panel that claims to be specific.
            if (p.Element(W + "pPr")?.Element(W + "rPr")?.Element(W + "ins") is not null) continue;

            if (p.Descendants(W + "ins").Any() || p.Descendants(W + "del").Any())
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
