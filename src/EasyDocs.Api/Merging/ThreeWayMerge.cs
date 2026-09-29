using System.Security.Cryptography;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;

namespace EasyDocs.Api.Merging;

/// <summary>
/// Pure: fold the incoming side's OWN changes (ancestor -> incoming) onto main, block by block, and
/// return main's package with that body plus the blocks both sides changed. The merge then runs
/// Compare(main, result), so the redline is exactly the incoming author's changes — never a reversion
/// of an edit main made and the incoming side simply did not have (the bug a plain two-way
/// Compare(main, incoming) has).
/// </summary>
/// <remarks>
/// The rule: the fold is either right or it REFUSES (NotSupportedException, which the merge turns into
/// its 409 "Merge unavailable" and the preview into available=false). It never silently loses,
/// duplicates or reverts content.
///
/// A diff3 over top-level body blocks (paragraphs, tables, content controls), aligned by visible text:
/// a block only one side changed takes that side's version; the same change on both sides is taken
/// once; both sides inserting at one point keeps both. Both sides changing the same PARAGRAPH
/// differently is a conflict: incoming's paragraph is proposed over main's as a tracked change
/// (rejecting it keeps main) and the block is reported in <see cref="Result.Overlaps"/>, which is what
/// the review screen lists. Everything the fold cannot guarantee is refused:
///  - both sides changed the same table or content control (they are single blocks here);
///  - a conflict whose alignment is ambiguous (its wording repeats elsewhere in the ancestor);
///  - a block one side moved and the other side changed (a block fold would keep both copies);
///  - an incoming block whose links, images or notes do not resolve identically in main's package;
///  - an incoming change to footnotes, endnotes, headers or footers (main's package is kept);
///  - a changed region too large to align (the LCS is quadratic).
///
/// ponytail: block-level, not word-level — two edits to different words of one paragraph are a
/// conflict (incoming's paragraph proposed over main's, and named). Not carried from the incoming side:
/// section/page setup (main's final sectPr is kept), new style or list definitions (its text lands,
/// possibly unstyled), and comments (WmlComparer drops comments on every path). Upgrade path:
/// recurse into w:tbl/w:tc and w:sdtContent when all three shapes agree, word-level three-way inside
/// conflicting paragraphs, remap renumbered relationship ids, Myers diff to lift the size cap.
/// </remarks>
public static class ThreeWayMerge
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // Ordinal is the ancestor's top-level body block index; Text labels it with the ancestor's wording.
    public record Overlap(int Ordinal, string Text);
    public record Result(byte[] Docx, IReadOnlyList<Overlap> Overlaps);

    // Cells of LCS table per alignment. 4M ints = 16 MB, ~2000 x 2000 changed blocks.
    private const long MaxCells = 4_000_000;

    public static Result Apply(byte[] ancestor, byte[] main, byte[] incoming)
    {
        using var ancDoc = WordprocessingDocument.Open(new MemoryStream(ancestor), false);
        using var incDoc = WordprocessingDocument.Open(new MemoryStream(incoming), false);
        if (NotesText(ancDoc) != NotesText(incDoc))
            throw new NotSupportedException("The incoming side changed footnotes or endnotes.");
        if (HeadersText(ancDoc) != HeadersText(incDoc))
            throw new NotSupportedException("The incoming side changed a header or footer.");

        using var ms = new MemoryStream();
        ms.Write(main);
        IReadOnlyList<Overlap> overlaps;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var part = doc.MainDocumentPart!;
            var xdoc = Load(part);
            var body = xdoc.Root!.Element(W + "body")!;
            var m = Blocks(xdoc);

            var fold = new Fold(Blocks(Load(ancDoc.MainDocumentPart!)), m, Blocks(Load(incDoc.MainDocumentPart!)),
                ancDoc.MainDocumentPart!, part, incDoc.MainDocumentPart!);
            var merged = fold.Run();
            overlaps = fold.Overlaps;

            var content = merged.Select(b => new XElement(b)).ToList();
            foreach (var e in m) e.Remove();
            body.AddFirst(content); // main's final sectPr stays last

            using var s = part.GetStream(FileMode.Create, FileAccess.Write);
            xdoc.Save(s, SaveOptions.DisableFormatting);
        }
        return new Result(ms.ToArray(), overlaps);
    }

    private record Hunk(int AStart, int AEnd, int XStart, int XEnd);

    private sealed class Fold(
        List<XElement> a, List<XElement> m, List<XElement> i,
        MainDocumentPart ap, MainDocumentPart mp, MainDocumentPart ip)
    {
        private readonly string[] ka = [.. a.Select(Key)], km = [.. m.Select(Key)], ki = [.. i.Select(Key)];
        private readonly string?[] na = new string?[a.Count], nm = new string?[m.Count], ni = new string?[i.Count];
        private readonly Dictionary<string, int> ancestorCount = a.Select(Key).CountBy(k => k).ToDictionary();
        private readonly List<XElement> result = [];
        public readonly List<Overlap> Overlaps = [];

        private string NA(int k) => na[k] ??= Norm(a[k], ap);
        private string NM(int k) => nm[k] ??= Norm(m[k], mp);
        private string NI(int k) => ni[k] ??= Norm(i[k], ip);

        public List<XElement> Run()
        {
            var (hm, mm) = Diff(ka, km);
            var (hi, mi) = Diff(ka, ki);
            RefuseMoves(hm, km, hi, mi, NI);
            RefuseMoves(hi, ki, hm, mm, NM);

            // Pure insertions sort before a change starting at the same point: inserted first.
            var all = hm.Select(h => (h, main: true)).Concat(hi.Select(h => (h, main: false)))
                .OrderBy(t => t.h.AStart).ThenBy(t => t.h.AEnd - t.h.AStart).ToList();

            var cursor = 0;
            for (var idx = 0; idx < all.Count;)
            {
                var group = new List<(Hunk h, bool main)> { all[idx++] };
                int gs = group[0].h.AStart, ge = group[0].h.AEnd;
                // Overlapping ancestor ranges conflict; so do two insertions at one point. Touching don't.
                while (idx < all.Count && (all[idx].h.AStart < ge
                       || (gs == ge && all[idx].h.AStart == ge && all[idx].h.AEnd == ge)))
                {
                    ge = Math.Max(ge, all[idx].h.AEnd);
                    group.Add(all[idx++]);
                }

                for (; cursor < gs; cursor++) Block(cursor, mm[cursor], mi[cursor]);
                cursor = ge;

                var (ms, me) = Segment(group.Where(t => t.main).Select(t => t.h).ToList(), mm, gs, ge);
                var (@is, ie) = Segment(group.Where(t => !t.main).Select(t => t.h).ToList(), mi, gs, ge);
                Settle(gs, ge, ms, me, @is, ie);
            }
            for (; cursor < a.Count; cursor++) Block(cursor, mm[cursor], mi[cursor]);
            return result;
        }

        // One ancestor block against its main and incoming counterparts (same shape on all three).
        private void Block(int k, int mk, int ik)
        {
            bool mChanged = NM(mk) != NA(k), iChanged = NI(ik) != NA(k);
            if (!iChanged) { result.Add(m[mk]); return; }
            if (!mChanged) { AddIncoming(i[ik]); return; }
            if (ancestorCount[ka[k]] > 1) throw Ambiguous();
            Overlaps.Add(new Overlap(k, Label(a[k])));
            if (NM(mk) == NI(ik)) { result.Add(m[mk]); return; }
            if (a[k].Name != W + "p" || m[mk].Name != W + "p" || i[ik].Name != W + "p")
                throw new NotSupportedException("Both sides changed the same table or content control.");
            AddIncoming(i[ik]); // conflict: incoming's paragraph, tracked over main's, reported above
        }

        private void Settle(int gs, int ge, int ms, int me, int @is, int ie)
        {
            int len = ge - gs, ml = me - ms, il = ie - @is;
            if (ml == len && il == len)
            {
                for (var d = 0; d < len; d++) Block(gs + d, ms + d, @is + d);
                return;
            }
            // One side left this range exactly as the ancestor had it: the other side's version stands.
            if (ml == len && Enumerable.Range(0, len).All(d => NM(ms + d) == NA(gs + d)))
            {
                for (var k = @is; k < ie; k++) AddIncoming(i[k]);
                return;
            }
            if (il == len && Enumerable.Range(0, len).All(d => NI(@is + d) == NA(gs + d)))
            {
                for (var k = ms; k < me; k++) result.Add(m[k]);
                return;
            }
            if (len == 0)
            {
                // Both inserted here: keep both, blocks they share once (main's copy), in order.
                var (_, match) = Diff(km[ms..me], ki[@is..ie]);
                var j = @is;
                for (var k = 0; k < match.Length; k++)
                {
                    if (match[k] >= 0)
                    {
                        for (; j < @is + match[k]; j++) AddIncoming(i[j]);
                        j++;
                    }
                    result.Add(m[ms + k]);
                }
                for (; j < ie; j++) AddIncoming(i[j]);
                return;
            }
            // Both sides changed this range. Which copy of a repeated paragraph each side meant is not
            // knowable, and guessing wrong resurrects a deletion or drops one side's change.
            if (ka[gs..ge].Any(k => ancestorCount[k] > 1)) throw Ambiguous();
            if (Enumerable.Range(ms, ml).Select(NM).SequenceEqual(Enumerable.Range(@is, il).Select(NI)))
            {
                for (var k = ms; k < me; k++) result.Add(m[k]); // the same change on both sides, once
                return;
            }

            // A genuine conflict of different shapes. Only paragraphs, and only when the alignment is
            // unambiguous; then incoming's version is proposed over main's and every block is named.
            if (!a[gs..ge].Concat(m[ms..me]).Concat(i[@is..ie]).All(e => e.Name == W + "p"))
                throw new NotSupportedException("Both sides changed the same table or content control.");
            // Likewise a side's new block whose wording also exists in the ancestor: it may be a copy the
            // alignment placed here rather than where the author put it.
            if (km[ms..me].Concat(ki[@is..ie]).Any(ancestorCount.ContainsKey)) throw Ambiguous();
            for (var k = gs; k < ge; k++) Overlaps.Add(new Overlap(k, Label(a[k])));
            for (var k = @is; k < ie; k++) AddIncoming(i[k]);
        }

        // A side's block range for ancestor range [gs, ge): its hunks plus the untouched blocks between
        // them, which map one-to-one — or, with no hunk here, simply its matched blocks.
        private static (int, int) Segment(List<Hunk> hs, int[] match, int gs, int ge)
        {
            if (hs.Count > 0) return (hs[0].XStart - (hs[0].AStart - gs), hs[^1].XEnd + (ge - hs[^1].AEnd));
            if (gs < ge) return (match[gs], match[gs] + (ge - gs));
            return (0, 0); // an empty range this side did not touch: nothing to place
        }

        private static NotSupportedException Ambiguous() =>
            new("Both sides changed paragraphs whose wording repeats; which copy each meant is ambiguous.");

        private void AddIncoming(XElement block)
        {
            if (!Portable(block, ip, mp))
                throw new NotSupportedException("An incoming change uses a link, image or note main does not share.");
            result.Add(block);
        }

        // Side X removed ancestor block k and re-inserted the same text elsewhere (a move), while the
        // other side changed k: a block fold would keep the moved copy AND the edited one.
        private void RefuseMoves(List<Hunk> hx, string[] kx, List<Hunk> hOther, int[] matchOther, Func<int, string> normOther)
        {
            var inserted = hx.SelectMany(h => kx[h.XStart..h.XEnd]).ToHashSet();
            var otherCovers = new bool[a.Count];
            foreach (var h in hOther) for (var k = h.AStart; k < h.AEnd; k++) otherCovers[k] = true;
            foreach (var h in hx)
                for (var k = h.AStart; k < h.AEnd; k++)
                    if (HasText(ka[k]) && inserted.Contains(ka[k])
                        && (otherCovers[k] || normOther(matchOther[k]) != NA(k)))
                        throw new NotSupportedException("One side moved a paragraph the other side changed.");
        }
    }

    private static bool HasText(string key) => key[^1] != '|';

    private static (List<Hunk> Hunks, int[] Match) Diff(string[] a, string[] x)
    {
        int n = a.Length, m = x.Length, pre = 0, suf = 0;
        while (pre < n && pre < m && a[pre] == x[pre]) pre++;
        while (suf < n - pre && suf < m - pre && a[n - 1 - suf] == x[m - 1 - suf]) suf++;
        int an = n - pre - suf, xn = m - pre - suf;
        if ((long)(an + 1) * (xn + 1) > MaxCells)
            throw new NotSupportedException("The changed region is too large to merge automatically.");

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

    // Everything that makes the block what it is, minus what Word rewrites without anyone editing
    // (rsids, w14 ids, proofing marks), with relationship ids replaced by what they point at — so an
    // image swap or a new link URL is a change, and a pure rId renumber is not.
    private static string Norm(XElement block, MainDocumentPart part)
    {
        var c = new XElement(block);
        c.Descendants().Where(e => e.Name == W + "proofErr" || e.Name == W + "lastRenderedPageBreak").Remove();
        foreach (var e in c.DescendantsAndSelf())
        {
            e.Attributes().Where(x => x.Name.LocalName.StartsWith("rsid") || x.Name.Namespace == W14).Remove();
            foreach (var x in e.Attributes().Where(x => x.Name.Namespace == R))
                x.Value = Target(part, x.Value) ?? "missing:" + x.Value;
        }
        return c.ToString(SaveOptions.DisableFormatting);
    }

    private static string NotesText(WordprocessingDocument doc) =>
        (doc.MainDocumentPart!.FootnotesPart is { } f ? Load(f).Root!.Value : "") + "\u0000" +
        (doc.MainDocumentPart!.EndnotesPart is { } e ? Load(e).Root!.Value : "");

    private static string HeadersText(WordprocessingDocument doc) =>
        string.Join("\u0000", doc.MainDocumentPart!.HeaderParts.Cast<OpenXmlPart>()
            .Concat(doc.MainDocumentPart!.FooterParts).Select(p => Load(p).Root!.Value).Order(StringComparer.Ordinal));

    // A block moved from the incoming package into main's must mean the same thing there: every
    // relationship id it uses resolves to the same target, every note it references reads the same.
    private static bool Portable(XElement block, MainDocumentPart from, MainDocumentPart to)
    {
        foreach (var id in block.DescendantsAndSelf().Attributes().Where(x => x.Name.Namespace == R).Select(x => x.Value))
            if (Target(from, id) is not { } t || t != Target(to, id))
                return false;

        foreach (var (name, pick) in new (XName, Func<MainDocumentPart, OpenXmlPart?>)[]
                 { (W + "footnoteReference", p => p.FootnotesPart), (W + "endnoteReference", p => p.EndnotesPart) })
            foreach (var id in block.Descendants(name).Select(r => (string?)r.Attribute(W + "id")))
                if (Note(pick(from), id) is not { } n || n != Note(pick(to), id))
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
        // Header/footer parts by text: their bytes are rewritten by every save. Anything else by bytes.
        if (child is HeaderPart or FooterPart) return "text:" + Load(child).Root!.Value;
        using var s = child.GetStream(FileMode.Open, FileAccess.Read);
        return "part:" + Convert.ToHexString(SHA256.HashData(s));
    }

    private static string? Note(OpenXmlPart? notes, string? id) =>
        notes is null ? null
            : Load(notes).Root!.Elements().FirstOrDefault(n => (string?)n.Attribute(W + "id") == id)?.Value;

    private const int LabelLength = 60;

    private static string Label(XElement block)
    {
        var text = string.Concat(block.Descendants(W + "t").Select(t => t.Value)).Trim();
        return text.Length <= LabelLength ? text : text[..LabelLength].TrimEnd() + "…";
    }
}
