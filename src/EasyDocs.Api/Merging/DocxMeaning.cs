using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace EasyDocs.Api.Merging;

/// <summary>
/// What a block MEANS in one package, as a comparable string: its visible text, the run formatting a
/// reader sees (resolved through styles and document defaults), its paragraph style, alignment and list
/// format, what its links and images point at, and the text of the notes it references.
/// </summary>
/// <remarks>
/// Why not the markup: LibreOffice — so every Collabora save — rewrites every paragraph on save (explicit
/// pStyle and empty rPr, renumbered bookmark and list ids, style properties moved between defaults,
/// styles and runs). Compared as markup, a paragraph nobody touched "changed", and the three-way fold
/// then read main's real edit as a conflict and resolved it the wrong way. Resolved to meaning, the
/// rewrite is equal and the edit is the only change.
///
/// ponytail: deliberately a SUBSET of formatting — b/i/u/strike/dstrike/caps/sz/color/highlight/
/// vertAlign, paragraph style id, jc, list format and level, table cell spans. Changes outside it
/// (fonts, spacing, indents, borders, shading, table widths) are invisible here, so an incoming change
/// that is ONLY one of those is not carried (main's version of that block is kept). Widen the subset
/// when a real case needs it; each property added must be checked against a LibreOffice round trip.
/// </remarks>
public sealed class DocxMeaning
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly string[] RunProps = ["b", "i", "u", "strike", "dstrike", "caps", "sz", "color", "highlight", "vertAlign"];

    private readonly OpenXmlPart part;
    private readonly Dictionary<string, XElement> styles;
    private readonly string defaultParagraphStyle;
    private readonly XElement? defaultRPr, defaultPPr;
    private readonly Dictionary<string, XElement> nums, abstractNums;
    private readonly Dictionary<string, string?> targets = [];
    private readonly Func<string?, string> footNote, endNote;

    public DocxMeaning(MainDocumentPart main) : this(main, main)
    {
    }

    // `part` resolves relationship ids (the main document part, or a header/footer part); styles,
    // numbering and notes always come from the main document part.
    public DocxMeaning(MainDocumentPart main, OpenXmlPart part)
    {
        this.part = part;
        var st = main.StyleDefinitionsPart is { } sp ? Load(sp).Root : null;
        styles = st?.Elements(W + "style").Where(s => s.Attribute(W + "styleId") is not null)
            .GroupBy(s => (string)s.Attribute(W + "styleId")!).ToDictionary(g => g.Key, g => g.First()) ?? [];
        defaultParagraphStyle = st?.Elements(W + "style").FirstOrDefault(s =>
                (string?)s.Attribute(W + "type") == "paragraph" && IsOn(s.Attribute(W + "default")?.Value))
            ?.Attribute(W + "styleId")?.Value ?? "Normal";
        defaultRPr = st?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        defaultPPr = st?.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr");

        var nb = main.NumberingDefinitionsPart is { } np ? Load(np).Root : null;
        nums = nb?.Elements(W + "num").GroupBy(n => (string?)n.Attribute(W + "numId") ?? "")
            .ToDictionary(g => g.Key, g => g.First()) ?? [];
        abstractNums = nb?.Elements(W + "abstractNum").GroupBy(n => (string?)n.Attribute(W + "abstractNumId") ?? "")
            .ToDictionary(g => g.Key, g => g.First()) ?? [];

        var foot = main.FootnotesPart is { } f ? Load(f).Root : null;
        var end = main.EndnotesPart is { } e ? Load(e).Root : null;
        footNote = id => foot?.Elements().FirstOrDefault(n => (string?)n.Attribute(W + "id") == id)?.Value ?? "?";
        endNote = id => end?.Elements().FirstOrDefault(n => (string?)n.Attribute(W + "id") == id)?.Value ?? "?";
    }

    public string Of(XElement block)
    {
        var sb = new StringBuilder();
        Walk(block, sb);
        return sb.ToString();
    }

    // What a relationship id points at: an external URI, or a hash of the target part's bytes. null
    // when the id does not exist in this part.
    public string? Target(string id)
    {
        if (targets.TryGetValue(id, out var t)) return t;
        var ext = part.ExternalRelationships.FirstOrDefault(r => r.Id == id)?.Uri
                  ?? part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri;
        if (ext is not null) return targets[id] = "ext:" + ext;
        var child = part.Parts.Where(p => p.RelationshipId == id).Select(p => p.OpenXmlPart).FirstOrDefault();
        if (child is null) return targets[id] = null;
        using var s = child.GetStream(FileMode.Open, FileAccess.Read);
        return targets[id] = "part:" + Convert.ToHexString(SHA256.HashData(s));
    }

    private void Walk(XElement e, StringBuilder sb)
    {
        if (e.Name == W + "p") { Paragraph(e, sb); return; }
        if (e.Name.LocalName.EndsWith("Pr")) return; // tblPr, trPr, sdtPr, …: layout, not meaning
        var mark = e.Name == W + "tbl" ? "T" : e.Name == W + "tr" ? "R" : e.Name == W + "tc" ? "C" + Span(e) : null;
        if (mark is not null) sb.Append('<').Append(mark);
        foreach (var c in e.Elements()) Walk(c, sb);
        if (mark is not null) sb.Append('>');
    }

    private static string Span(XElement tc)
    {
        var pr = tc.Element(W + "tcPr");
        var span = (string?)pr?.Element(W + "gridSpan")?.Attribute(W + "val") ?? "1";
        var vm = pr?.Element(W + "vMerge");
        return span + (vm is null ? "" : (string?)vm.Attribute(W + "val") == "restart" ? "v" : "^");
    }

    private void Paragraph(XElement p, StringBuilder sb)
    {
        var ppr = p.Element(W + "pPr");
        var style = (string?)ppr?.Element(W + "pStyle")?.Attribute(W + "val") ?? defaultParagraphStyle;
        var chain = Chain(style);

        var numPr = ppr?.Element(W + "numPr") ?? chain.Select(s => s.Element(W + "pPr")?.Element(W + "numPr")).FirstOrDefault(n => n is not null);
        var jc = (string?)(ppr?.Element(W + "jc") ?? chain.Select(s => s.Element(W + "pPr")?.Element(W + "jc")).FirstOrDefault(j => j is not null)
                           ?? defaultPPr?.Element(W + "jc"))?.Attribute(W + "val");
        sb.Append("P(").Append(style).Append('|').Append(ListOf(numPr)).Append('|')
          .Append(jc is null or "left" or "start" ? "" : jc == "end" ? "right" : jc).Append(')');

        string? lastFmt = null;
        foreach (var r in p.Descendants(W + "r").Where(r => r.Ancestors(W + "p").First() == p))
        {
            var content = RunContent(r);
            if (content.Length == 0) continue;
            var fmt = RunFormat(r, chain);
            if (fmt != lastFmt) sb.Append('{').Append(fmt).Append('}');
            sb.Append(content);
            lastFmt = fmt;
        }
    }

    private string RunContent(XElement r)
    {
        var sb = new StringBuilder();
        foreach (var c in r.Elements())
        {
            if (c.Name == W + "t") sb.Append(c.Value);
            else if (c.Name == W + "tab") sb.Append('\t');
            else if (c.Name == W + "br" || c.Name == W + "cr") sb.Append('\n');
            else if (c.Name == W + "sym") sb.Append((string?)c.Attribute(W + "char"));
            else if (c.Name == W + "footnoteReference") sb.Append("[fn:").Append(footNote((string?)c.Attribute(W + "id"))).Append(']');
            else if (c.Name == W + "endnoteReference") sb.Append("[en:").Append(endNote((string?)c.Attribute(W + "id"))).Append(']');
            else if (c.Name == W + "drawing" || c.Name == W + "pict" || c.Name == W + "object")
            {
                sb.Append("[obj");
                foreach (var a in c.Descendants().Attributes().Where(a => a.Name.Namespace == R))
                    sb.Append(' ').Append(Target(a.Value) ?? "missing");
                sb.Append(']');
            }
        }
        return sb.ToString();
    }

    private string RunFormat(XElement r, List<XElement> paragraphChain)
    {
        var rpr = r.Element(W + "rPr");
        var rStyle = (string?)rpr?.Element(W + "rStyle")?.Attribute(W + "val");
        var layers = new List<XElement?> { rpr };
        if (rStyle is not null) layers.AddRange(Chain(rStyle).Select(s => s.Element(W + "rPr")));
        layers.AddRange(paragraphChain.Select(s => s.Element(W + "rPr")));
        layers.Add(defaultRPr);

        var sb = new StringBuilder();
        var link = r.Ancestors(W + "hyperlink").FirstOrDefault();
        if (link is not null)
            sb.Append("link=").Append(link.Attribute(R + "id") is { } id ? Target(id.Value) ?? "missing" : "#" + (string?)link.Attribute(W + "anchor")).Append(';');
        foreach (var name in RunProps)
        {
            var el = layers.Select(l => l?.Element(W + name)).FirstOrDefault(x => x is not null);
            var v = Normal(name, el);
            if (v is not null) sb.Append(name).Append('=').Append(v).Append(';');
        }
        return sb.ToString();
    }

    // A property's value as a reader sees it, null for "the default" (off, auto, none, baseline, 10pt).
    private static string? Normal(string name, XElement? el)
    {
        var v = (string?)el?.Attribute(W + "val");
        return name switch
        {
            "b" or "i" or "strike" or "dstrike" or "caps" => el is not null && (v is null || IsOn(v)) ? "1" : null,
            "u" => el is null || v == "none" ? null : v ?? "single",
            "sz" => v is null or "20" ? null : v,
            "color" => v is null or "auto" or "000000" ? null : v.ToUpperInvariant(),
            "highlight" => v is null or "none" ? null : v,
            "vertAlign" => v is null or "baseline" ? null : v,
            _ => v,
        };
    }

    // A list paragraph's level, number format and level text — what the reader sees, not the numId.
    public string ListOf(XElement? numPr)
    {
        var numId = (string?)numPr?.Element(W + "numId")?.Attribute(W + "val");
        if (numId is null or "0") return "";
        var ilvl = (string?)numPr!.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
        if (!nums.TryGetValue(numId, out var num)) return "?";
        var absId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val") ?? "";
        var lvl = (num.Elements(W + "lvlOverride").FirstOrDefault(o => (string?)o.Attribute(W + "ilvl") == ilvl)?.Element(W + "lvl"))
                  ?? (abstractNums.TryGetValue(absId, out var abs)
                      ? abs.Elements(W + "lvl").FirstOrDefault(l => (string?)l.Attribute(W + "ilvl") == ilvl)
                      : null);
        if (lvl is null) return "?";
        return ilvl + ":" + (string?)lvl.Element(W + "numFmt")?.Attribute(W + "val") + ":" + (string?)lvl.Element(W + "lvlText")?.Attribute(W + "val");
    }

    // The style and the styles it is based on, most specific first.
    private List<XElement> Chain(string styleId)
    {
        var chain = new List<XElement>();
        for (var id = styleId; id is not null && chain.Count < 20 && styles.TryGetValue(id, out var s);
             id = (string?)s.Element(W + "basedOn")?.Attribute(W + "val"))
            chain.Add(s);
        return chain;
    }

    private static bool IsOn(string? v) => v is "1" or "true" or "on";

    internal static XDocument Load(OpenXmlPart part)
    {
        using var s = part.GetStream(FileMode.Open, FileAccess.Read);
        return XDocument.Load(s);
    }
}
