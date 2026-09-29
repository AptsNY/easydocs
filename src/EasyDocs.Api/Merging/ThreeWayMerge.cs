using System.Security.Cryptography;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace EasyDocs.Api.Merging;

/// <summary>
/// Pure: fold the incoming side's OWN changes (ancestor -> incoming) onto main, block by block, and
/// return main's package with that body. The merge then runs Compare(main, result), so the redline is
/// exactly the incoming author's changes — never a reversion of an edit main made and the incoming
/// side simply did not have (the bug a plain two-way Compare(main, incoming) has).
/// </summary>
/// <remarks>
/// A diff3 over top-level body blocks (paragraphs, tables, …), aligned by their visible text:
/// a block only one side changed takes that side's version; the same change on both sides is taken
/// once; both sides inserting at the same point keeps both. A genuine conflict — both sides changed the
/// same blocks differently — takes the INCOMING version, so it shows as a tracked replacement of
/// main's text that rejecting restores; the preview's overlap hint is what names it before the click.
///
/// ponytail: block-level, not word-level. Two edits to different words of ONE paragraph are a conflict
/// here (incoming's paragraph wins, main's word edit shows as a tracked reversion), and a block's
/// formatting-only change survives only when the other side left that block byte-identical (rsids
/// aside). Parts outside the body come from main: B's new styles or list definitions are not carried
/// over (its text lands, possibly unstyled), and a B-side footnote/endnote edit is refused rather than
/// dropped. Upgrade path: word-level three-way inside conflicting paragraphs, and copying missing
/// styles/numbering from the incoming package.
/// </remarks>
public static class ThreeWayMerge
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // Throws NotSupportedException when the result could not faithfully carry the incoming side's
    // changes; the merge service turns every throw into its "merge unavailable" 409.
    public static byte[] Apply(byte[] ancestor, byte[] main, byte[] incoming)
    {
        using var ancDoc = WordprocessingDocument.Open(new MemoryStream(ancestor), false);
        using var incDoc = WordprocessingDocument.Open(new MemoryStream(incoming), false);
        if (NotesText(ancDoc) != NotesText(incDoc))
            throw new NotSupportedException("The incoming side changed footnotes or endnotes.");

        var a = Blocks(Load(ancDoc.MainDocumentPart!));
        var i = Blocks(Load(incDoc.MainDocumentPart!));

        using var ms = new MemoryStream();
        ms.Write(main);
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var part = doc.MainDocumentPart!;
            var xdoc = Load(part);
            var body = xdoc.Root!.Element(W + "body")!;
            var m = Blocks(xdoc);

            var merged = Merge(a, m, i, b => Portable(b, incDoc, doc));
            foreach (var (block, fromIncoming) in merged)
                if (fromIncoming && !Portable(block, incDoc, doc))
                    throw new NotSupportedException("An incoming block references a relationship or note main does not share.");

            var content = merged.Select(x => new XElement(x.Block)).ToList();
            foreach (var e in m) e.Remove();
            body.AddFirst(content); // main's final sectPr stays last

            using var s = part.GetStream(FileMode.Create, FileAccess.Write);
            xdoc.Save(s, SaveOptions.DisableFormatting);
        }
        return ms.ToArray();
    }

    private record Hunk(int AStart, int AEnd, int XStart, int XEnd);

    private static List<(XElement Block, bool FromIncoming)> Merge(
        List<XElement> a, List<XElement> m, List<XElement> i, Func<XElement, bool> portable)
    {
        string[] ka = [.. a.Select(Key)], km = [.. m.Select(Key)], ki = [.. i.Select(Key)];
        var (hm, mm) = Diff(ka, km);
        var (hi, mi) = Diff(ka, ki);

        // Empty (pure-insertion) hunks sort before a change starting at the same point: inserted first.
        var all = hm.Select(h => (h, main: true)).Concat(hi.Select(h => (h, main: false)))
            .OrderBy(t => t.h.AStart).ThenBy(t => t.h.AEnd - t.h.AStart).ToList();

        var result = new List<(XElement, bool)>();
        var cursor = 0;

        void Unchanged(int to)
        {
            for (; cursor < to; cursor++)
            {
                // Same text on all three sides. Take incoming only for a change main did not also make,
                // and only when it carries over — a renumbered image rId is noise, not a reason to 409.
                var (ab, mb, ib) = (a[cursor], m[mm[cursor]], i[mi[cursor]]);
                var incomingOnly = Norm(mb) == Norm(ab) && Norm(ib) != Norm(ab) && portable(ib);
                result.Add(incomingOnly ? (ib, true) : (mb, false));
            }
        }
        void Emit(List<XElement> side, int from, int to, bool incoming)
        {
            for (var k = from; k < to; k++) result.Add((side[k], incoming));
        }

        for (var idx = 0; idx < all.Count;)
        {
            var group = new List<(Hunk h, bool main)> { all[idx++] };
            int gs = group[0].h.AStart, ge = group[0].h.AEnd;
            // Overlapping ancestor ranges conflict; so do two insertions at one point. Touching ranges don't.
            while (idx < all.Count && (all[idx].h.AStart < ge
                   || (gs == ge && all[idx].h.AStart == ge && all[idx].h.AEnd == ge)))
            {
                ge = Math.Max(ge, all[idx].h.AEnd);
                group.Add(all[idx++]);
            }

            Unchanged(gs);
            cursor = ge;

            var gm = group.Where(t => t.main).Select(t => t.h).ToList();
            var gi = group.Where(t => !t.main).Select(t => t.h).ToList();
            if (gi.Count == 0) { Emit(m, gm[0].XStart, gm[0].XEnd, false); continue; }
            if (gm.Count == 0) { Emit(i, gi[0].XStart, gi[0].XEnd, true); continue; }

            var (ms, me) = Span(gm, gs, ge);
            var (@is, ie) = Span(gi, gs, ge);
            if (km[ms..me].SequenceEqual(ki[@is..ie])) Emit(m, ms, me, false); // the same change, once
            else if (gs == ge)
            {
                // Both inserted here: keep both, blocks they share once (main's copy), in order.
                var (_, match) = Diff(km[ms..me], ki[@is..ie]);
                var j = @is;
                for (var k = 0; k < match.Length; k++)
                {
                    if (match[k] < 0) { result.Add((m[ms + k], false)); continue; }
                    Emit(i, j, @is + match[k], true);
                    result.Add((m[ms + k], false));
                    j = @is + match[k] + 1;
                }
                Emit(i, j, ie, true);
            }
            else Emit(i, @is, ie, true); // genuine conflict: incoming's version, tracked over main's
        }
        Unchanged(a.Count);
        return result;
    }

    // One side's block range covering ancestor range [gs, ge) — its hunks plus the untouched blocks
    // between them, which map one-to-one.
    private static (int, int) Span(List<Hunk> hs, int gs, int ge) =>
        (hs[0].XStart - (hs[0].AStart - gs), hs[^1].XEnd + (ge - hs[^1].AEnd));

    // ponytail: quadratic LCS (after trimming the common prefix/suffix, which is most of a document).
    // Fine for contracts; a Myers diff if a merge of thousands of changed paragraphs ever shows up.
    private static (List<Hunk> Hunks, int[] Match) Diff(string[] a, string[] x)
    {
        int n = a.Length, m = x.Length, pre = 0, suf = 0;
        while (pre < n && pre < m && a[pre] == x[pre]) pre++;
        while (suf < n - pre && suf < m - pre && a[n - 1 - suf] == x[m - 1 - suf]) suf++;
        int an = n - pre - suf, xn = m - pre - suf;

        var lcs = new int[an + 1, xn + 1];
        for (var p = an - 1; p >= 0; p--)
            for (var q = xn - 1; q >= 0; q--)
                lcs[p, q] = a[pre + p] == x[pre + q] ? lcs[p + 1, q + 1] + 1 : Math.Max(lcs[p + 1, q], lcs[p, q + 1]);

        var match = Enumerable.Repeat(-1, n).ToArray();
        for (var k = 0; k < pre; k++) match[k] = k;
        for (var k = 0; k < suf; k++) match[n - 1 - k] = m - 1 - k;
        for (int p = 0, q = 0; p < an && q < xn;)
        {
            if (a[pre + p] == x[pre + q]) match[pre + p++] = pre + q++;
            else if (lcs[p + 1, q] >= lcs[p, q + 1]) p++;
            else q++;
        }

        var hunks = new List<Hunk>();
        for (int k = 0, ai = 0, xi = 0; k <= n; k++)
        {
            if (k < n && match[k] < 0) continue;
            var xk = k == n ? m : match[k];
            if (ai < k || xi < xk) hunks.Add(new Hunk(ai, k, xi, xk));
            ai = k + 1;
            xi = xk + 1;
        }
        return (hunks, match);
    }

    private static XDocument Load(OpenXmlPart part)
    {
        using var s = part.GetStream(FileMode.Open, FileAccess.Read);
        return XDocument.Load(s);
    }

    private static List<XElement> Blocks(XDocument doc) =>
        doc.Root!.Element(W + "body")!.Elements().Where(e => e.Name != W + "sectPr").ToList();

    // Visible content: what alignment keys on. Formatting and rsid noise are deliberately not in it.
    private static string Key(XElement block) =>
        block.Name.LocalName + "|" + string.Concat(block.Descendants().Select(d =>
            d.Name == W + "t" ? d.Value
            : d.Parent?.Name != W + "r" ? (d.Name == W + "p" ? "¶" : "")
            : d.Name == W + "tab" ? "\t"
            : d.Name == W + "br" || d.Name == W + "cr" ? "\n"
            : d.Name == W + "drawing" || d.Name == W + "pict" || d.Name == W + "object" ? "￼"
            : ""));

    // The block's markup minus what Word rewrites without anyone editing (rsids, w14 para/text ids).
    private static string Norm(XElement block)
    {
        var c = new XElement(block);
        foreach (var e in c.DescendantsAndSelf())
            e.Attributes().Where(x => x.Name.LocalName.StartsWith("rsid") || x.Name.Namespace == W14).Remove();
        return c.ToString(SaveOptions.DisableFormatting);
    }

    private static string NotesText(WordprocessingDocument doc) =>
        (doc.MainDocumentPart!.FootnotesPart is { } f ? Load(f).Root!.Value : "") + "\u0000" +
        (doc.MainDocumentPart!.EndnotesPart is { } e ? Load(e).Root!.Value : "");

    // A block moved from the incoming package into main's must mean the same thing there: every
    // relationship id it uses resolves to the same target, every note it references reads the same.
    private static bool Portable(XElement block, WordprocessingDocument from, WordprocessingDocument to)
    {
        foreach (var id in block.DescendantsAndSelf().Attributes().Where(x => x.Name.Namespace == R).Select(x => x.Value))
            if (Target(from.MainDocumentPart!, id) is not { } t || t != Target(to.MainDocumentPart!, id))
                return false;

        foreach (var (name, pick) in new (XName, Func<MainDocumentPart, OpenXmlPart?>)[]
                 { (W + "footnoteReference", p => p.FootnotesPart), (W + "endnoteReference", p => p.EndnotesPart) })
            foreach (var id in block.Descendants(name).Select(r => (string?)r.Attribute(W + "id")))
                if (Note(pick(from.MainDocumentPart!), id) is not { } n || n != Note(pick(to.MainDocumentPart!), id))
                    return false;
        return true;
    }

    private static string? Target(OpenXmlPart part, string id)
    {
        var ext = part.ExternalRelationships.FirstOrDefault(r => r.Id == id)?.Uri
                  ?? part.HyperlinkRelationships.FirstOrDefault(r => r.Id == id)?.Uri;
        if (ext is not null) return "ext:" + ext;
        var child = part.Parts.Where(p => p.RelationshipId == id).Select(p => p.OpenXmlPart).FirstOrDefault();
        if (child is null) return null;
        using var s = child.GetStream(FileMode.Open, FileAccess.Read);
        return "part:" + Convert.ToHexString(SHA256.HashData(s));
    }

    private static string? Note(OpenXmlPart? notes, string? id) =>
        notes is null ? null
            : Load(notes).Root!.Elements().FirstOrDefault(n => (string?)n.Attribute(W + "id") == id)?.Value;
}
