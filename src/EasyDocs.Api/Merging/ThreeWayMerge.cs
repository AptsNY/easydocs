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
/// A diff3 over top-level body blocks (paragraphs, tables, content controls), aligned by visible text
/// and compared by MEANING (<see cref="DocxMeaning"/>: text, visible run formatting, style, list format,
/// link/image targets) — never by markup, because every Collabora (LibreOffice) save rewrites the
/// markup of paragraphs nobody touched. A block only one side changed takes that side's version; the
/// same change on both sides is taken once; both sides inserting at one point keeps both. Both sides
/// changing the WORDS of the same paragraph differently is a conflict: incoming's paragraph is proposed
/// over main's as a tracked change (rejecting it keeps main) and the block is reported in
/// <see cref="Result.Overlaps"/>, which is what the review screen lists. Everything the fold cannot
/// guarantee is refused:
///  - both sides changed the same table or content control (they are single blocks here);
///  - one side reformatted a paragraph whose words the other side changed;
///  - changes among repeated paragraphs (blank lines, repeated signature lines) whose placement is
///    ambiguous — both sides changing a repeated paragraph, or changes from the two sides that could
///    "slide" into each other across a run of identical paragraphs;
///  - a block one side moved and the other side changed (a block fold would keep both copies);
///  - an incoming block whose links, images, notes or list format do not resolve identically in main's
///    package (a list item whose numId LibreOffice renumbered is re-pointed at the neighbouring list of
///    the same format first);
///  - an incoming change to footnotes, endnotes, or a displayed header or footer (main's package is kept);
///  - a changed region too large to align (the LCS is quadratic: ~2000 x 2000 changed blocks).
///
/// ponytail: block-level, not word-level — two edits to different words of one paragraph, or one side
/// splitting a paragraph the other edited, are a conflict (incoming's version proposed over main's, and
/// named). Formatting outside DocxMeaning's subset (fonts, spacing, indents, borders, table layout,
/// theme colours, small caps) is invisible, so an incoming change that is only that is not carried —
/// and where the incoming side edited a block main did not change in meaning, incoming's copy is taken:
/// main's paragraph properties and table properties/grid are carried onto it (AddIncoming), but main's
/// RUN formatting outside the subset (fonts, theme colours, small caps) and cell properties (tcW) are
/// not, and can revert. Same as the old two-way compare; the review screen says so. Not carried from the incoming
/// side either: section/page setup (main's final sectPr is kept), new style or list definitions, and
/// comments (WmlComparer drops comments on every path). Refusals are conservative on repeated wording.
/// Upgrade path: recurse into w:tbl/w:tc and w:sdtContent when all three shapes agree, word-level
/// three-way inside conflicting paragraphs, remap renumbered relationship ids, Myers diff for the cap.
/// </remarks>
public static class ThreeWayMerge
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
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
        if (Headers(ancDoc) != Headers(incDoc))
            throw new NotSupportedException("The incoming side changed a header or footer.");

        using var ms = new MemoryStream();
        ms.Write(main);
        IReadOnlyList<Overlap> overlaps;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var part = doc.MainDocumentPart!;
            var xdoc = Load(part);
            var body = xdoc.Root!.Element(W + "body")!;
            AnchorRangeMarkers(body);
            var m = Blocks(xdoc);

            var ancBody = Load(ancDoc.MainDocumentPart!);
            var incBody = Load(incDoc.MainDocumentPart!);
            AnchorRangeMarkers(ancBody.Root!.Element(W + "body")!);
            AnchorRangeMarkers(incBody.Root!.Element(W + "body")!);

            var fold = new Fold(Blocks(ancBody), m, Blocks(incBody),
                new DocxMeaning(ancDoc.MainDocumentPart!), new DocxMeaning(part), new DocxMeaning(incDoc.MainDocumentPart!));
            var merged = fold.Run();
            overlaps = fold.Overlaps;

            var fromMain = new HashSet<XElement>(m);
            var content = merged.Select(b => new XElement(b)).ToList();
            UniqueBookmarks(content, [.. merged.Select(fromMain.Contains)]);
            foreach (var e in m) e.Remove();
            body.AddFirst(content); // main's final sectPr stays last

            using var s = part.GetStream(FileMode.Create, FileAccess.Write);
            xdoc.Save(s, SaveOptions.DisableFormatting);
        }
        return new Result(ms.ToArray(), overlaps);
    }

    private record Hunk(int AStart, int AEnd, int XStart, int XEnd);

    private static readonly HashSet<string> RangeMarkers =
    [
        "bookmarkStart", "bookmarkEnd", "proofErr", "permStart", "permEnd", "commentRangeStart", "commentRangeEnd",
        "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd",
    ];

    // Range markers written directly in w:body (pandoc does this) are not blocks, and LibreOffice moves
    // them into the adjacent paragraph on save — so, compared as blocks, a save read as "deleted a
    // block" on both sides. Anchor them the way LibreOffice does, before aligning: a start marker at
    // the start of the next paragraph, an end marker at the end of the previous one (the other way at
    // a document edge). Proofing marks carry nothing and are dropped. Main's body is the output, so
    // main's anchors are kept, just inside a paragraph instead of between two.
    private static void AnchorRangeMarkers(XElement body)
    {
        foreach (var mark in body.Elements().Where(e => e.Name.Namespace == W && RangeMarkers.Contains(e.Name.LocalName)).ToList())
        {
            var before = mark.ElementsBeforeSelf(W + "p").LastOrDefault();
            var after = mark.ElementsAfterSelf(W + "p").FirstOrDefault();
            mark.Remove();
            if (mark.Name == W + "proofErr") continue;
            var target = mark.Name.LocalName.EndsWith("End") ? before ?? after : after ?? before;
            if (target is null) continue; // a body with no paragraph at all has nothing to anchor to
            if (target != after) target.Add(mark);
            else if (target.Element(W + "pPr") is { } ppr) ppr.AddAfterSelf(mark);
            else target.AddFirst(mark);
        }
    }

    // Blocks from two packages each number their bookmarks independently (LibreOffice renumbers on
    // every save). Keep main's; give incoming bookmarks whose id main already uses a fresh id, and drop
    // an incoming bookmark whose NAME main already has — two anchors of one name is not a document.
    private static void UniqueBookmarks(List<XElement> content, List<bool> fromMain)
    {
        var mainBlocks = content.Where((_, k) => fromMain[k]).ToList();
        var incomingBlocks = content.Where((_, k) => !fromMain[k]).ToList();
        var ids = mainBlocks.SelectMany(b => b.DescendantsAndSelf()).Where(IsBookmark)
            .Select(e => (string?)e.Attribute(W + "id")).OfType<string>().ToHashSet();
        var names = mainBlocks.SelectMany(b => b.Descendants(W + "bookmarkStart"))
            .Select(e => (string?)e.Attribute(W + "name")).OfType<string>().ToHashSet();

        var marks = incomingBlocks.SelectMany(b => b.DescendantsAndSelf()).Where(IsBookmark).ToList();
        var dropped = marks.Where(e => e.Name == W + "bookmarkStart" && names.Contains((string?)e.Attribute(W + "name") ?? ""))
            .Select(e => (string?)e.Attribute(W + "id")).ToHashSet();
        var next = ids.Select(v => int.TryParse(v, out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        var renumbered = new Dictionary<string, string>();
        foreach (var e in marks)
        {
            var id = (string?)e.Attribute(W + "id") ?? "";
            if (dropped.Contains(id)) { e.Remove(); continue; }
            if (!ids.Contains(id)) continue;
            if (!renumbered.TryGetValue(id, out var fresh)) renumbered[id] = fresh = (next++).ToString();
            e.SetAttributeValue(W + "id", fresh);
        }
    }

    private static bool IsBookmark(XElement e) => e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd";

    private sealed class Fold(
        List<XElement> a, List<XElement> m, List<XElement> i,
        DocxMeaning ap, DocxMeaning mp, DocxMeaning ip)
    {
        private readonly string[] ka = [.. a.Select(Key)], km = [.. m.Select(Key)], ki = [.. i.Select(Key)];
        private readonly string?[] na = new string?[a.Count], nm = new string?[m.Count], ni = new string?[i.Count];
        private readonly Dictionary<string, int> ancestorCount = a.Select(Key).CountBy(k => k).ToDictionary();
        private readonly List<XElement> result = [];
        public readonly List<Overlap> Overlaps = [];

        // A block's meaning (DocxMeaning), not its markup: an editor save rewrites markup wholesale.
        private string NA(int k) => na[k] ??= ap.Of(a[k]);
        private string NM(int k) => nm[k] ??= mp.Of(m[k]);
        private string NI(int k) => ni[k] ??= ip.Of(i[k]);

        public List<XElement> Run()
        {
            var (hm, mm) = Diff(ka, km);
            var (hi, mi) = Diff(ka, ki);
            RefuseMoves(hm, km, hi, mi, NI);
            RefuseMoves(hi, ki, hm, mm, NM);
            // Formatting-only changes (same words, different meaning) are not hunks, but they slide
            // among identical paragraphs just the same.
            RefuseSlidingNeighbours([.. hm, .. Restyled(mm, NM)], km, [.. hi, .. Restyled(mi, NI)], ki);

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
            if (!mChanged) { AddIncoming(i[ik], m[mk]); return; }
            Overlaps.Add(new Overlap(k, Label(a[k])));
            if (NM(mk) == NI(ik)) { result.Add(m[mk]); return; } // the same change on both sides, once
            if (ancestorCount[ka[k]] > 1) throw Ambiguous();
            if (a[k].Name != W + "p" || m[mk].Name != W + "p" || i[ik].Name != W + "p")
                throw new NotSupportedException($"Both sides changed the same {Describe(a[k].Name != W + "p" ? a[k] : m[mk].Name != W + "p" ? m[mk] : i[ik])}, which is merged only whole.");
            // Proposing incoming's paragraph is only honest when both sides changed its WORDS. If one
            // side only reformatted it, taking incoming would revert main's wording (or its formatting)
            // for a change the reviewer may not even see — refuse instead.
            if (km[mk] == ka[k] || ki[ik] == ka[k])
                throw new NotSupportedException("One side reformatted a paragraph the other side edited.");
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
            // Both sides inserted or deleted blocks here. Among repeated paragraphs which copy each side
            // meant is not knowable — two people each deleting "an Initials line" may have meant the
            // same line or two different ones — and guessing wrong resurrects a deletion or drops one
            // side's change. (Block() settles a same-shape identical change before asking this; a
            // change of SHAPE on repeated wording is where the ambiguity is real.)
            if (ka[gs..ge].Any(k => ancestorCount[k] > 1)) throw Ambiguous();
            if (Enumerable.Range(ms, ml).Select(NM).SequenceEqual(Enumerable.Range(@is, il).Select(NI)))
            {
                for (var k = ms; k < me; k++) result.Add(m[k]); // the same change on both sides, once
                return;
            }

            // A genuine conflict of different shapes. Only paragraphs, and only when the alignment is
            // unambiguous; then incoming's version is proposed over main's and every block is named.
            if (a[gs..ge].Concat(m[ms..me]).Concat(i[@is..ie]).FirstOrDefault(e => e.Name != W + "p") is { } other)
                throw new NotSupportedException($"Both sides changed the same region, and it holds a {Describe(other)}, which is merged only whole.");
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

        private static string Describe(XElement block) =>
            block.Name == W + "tbl" ? "table" : block.Name == W + "sdt" ? "content control" : $"<w:{block.Name.LocalName}>";

        private static NotSupportedException Ambiguous() =>
            new("Both sides changed paragraphs whose wording repeats; which copy each meant is ambiguous.");

        // `mainTwin`: main's copy of the same block, when main did not change what it means. Main may
        // still have changed its layout outside DocxMeaning's subset (an indent, spacing, column widths),
        // so that layout is carried onto incoming's copy — as long as the result still means exactly
        // what incoming's copy meant. Otherwise incoming's copy is taken as it is.
        private void AddIncoming(XElement block, XElement? mainTwin = null)
        {
            if (mainTwin is not null && WithLayoutOf(block, mainTwin) is { } laidOut && Portable(block, laidOut))
                result.Add(laidOut);
            else
                result.Add(Portable(block, block) ? block : Relisted(block)
                    ?? throw new NotSupportedException("An incoming change uses a link, image, note or list main does not share."));
        }

        private static XElement? WithLayoutOf(XElement block, XElement twin)
        {
            if (block.Name != twin.Name) return null;
            var copy = new XElement(block);
            if (block.Name == W + "p")
            {
                copy.Element(W + "pPr")?.Remove();
                if (twin.Element(W + "pPr") is { } ppr) copy.AddFirst(new XElement(ppr));
                return copy;
            }
            if (block.Name == W + "tbl")
            {
                copy.Element(W + "tblPr")?.Remove();
                if (twin.Element(W + "tblPr") is { } tblPr) copy.AddFirst(new XElement(tblPr));
                // Main's column grid only when it still describes this table's columns.
                if (twin.Element(W + "tblGrid") is { } grid && copy.Element(W + "tblGrid") is { } own
                    && grid.Elements(W + "gridCol").Count() == own.Elements(W + "gridCol").Count())
                    own.ReplaceWith(new XElement(grid));
                return copy;
            }
            return null;
        }

        // LibreOffice renumbers list ids on every save, so an incoming list item's numId can mean
        // another list — or nothing — in main's package. Re-point it at the list of the nearest
        // preceding paragraph already placed with the same list format (an item added to an existing
        // list, the common case); anything else still refuses.
        private XElement? Relisted(XElement block)
        {
            if (block.Name != W + "p" || block.Element(W + "pPr")?.Element(W + "numPr") is not { } numPr) return null;
            var want = ip.ListOf(numPr);
            var host = Enumerable.Reverse(result).Where(b => b.Name == W + "p")
                .Select(b => b.Element(W + "pPr")?.Element(W + "numPr"))
                .FirstOrDefault(n => n is not null && mp.ListOf(n) == want);
            if (host?.Element(W + "numId")?.Attribute(W + "val")?.Value is not { } numId) return null;
            var copy = new XElement(block);
            copy.Element(W + "pPr")!.Element(W + "numPr")!.Element(W + "numId")?.SetAttributeValue(W + "val", numId);
            return Portable(block, copy) ? copy : null;
        }

        // An incoming block placed in main's package (as `placed`, possibly adjusted) must mean there
        // exactly what it meant in incoming's: the same styles, list formats, link and image targets and
        // note text — and every relationship id it brought must exist in main with the same target.
        private bool Portable(XElement block, XElement placed) =>
            ip.Of(block) == mp.Of(placed)
            && placed.DescendantsAndSelf().Attributes().Where(x => x.Name.Namespace == R)
                .All(x => mp.Target(x.Value) is { } t && (!Rels(block).Contains(x.Value) || ip.Target(x.Value) == t));

        private static HashSet<string> Rels(XElement block) =>
            [.. block.DescendantsAndSelf().Attributes().Where(x => x.Name.Namespace == R).Select(x => x.Value)];

        // Two changes from different sides that do not overlap can still be ORDERED wrongly: next to a
        // run of identical paragraphs (blank lines, repeated signature lines) a change can "slide" —
        // the alignment could equally have put it one block up or down — and the fold would place
        // one side's new paragraph on the wrong side of the other's. Each change is widened over the
        // neighbouring blocks it could slide across; widened changes from the two sides that meet
        // are refused. A change among distinct paragraphs cannot slide, so this costs nothing there.
        private IEnumerable<Hunk> Restyled(int[] match, Func<int, string> norm) =>
            Enumerable.Range(0, a.Count).Where(k => match[k] >= 0 && norm(match[k]) != NA(k))
                .Select(k => new Hunk(k, k + 1, match[k], match[k] + 1));

        private void RefuseSlidingNeighbours(List<Hunk> hm, string[] km, List<Hunk> hi, string[] ki)
        {
            (int Lo, int Hi, bool Slides) Widen(Hunk h, string[] kx)
            {
                var keys = ka[h.AStart..h.AEnd].Concat(kx[h.XStart..h.XEnd]).ToHashSet();
                int lo = h.AStart, hi = h.AEnd;
                while (lo > 0 && keys.Contains(ka[lo - 1])) lo--;
                while (hi < a.Count && keys.Contains(ka[hi])) hi++;
                return (lo, hi, lo != h.AStart || hi != h.AEnd);
            }
            static bool Grouped(Hunk x, Hunk y) =>
                (x.AStart < y.AEnd && y.AStart < x.AEnd) || (x.AStart == x.AEnd && y.AStart == y.AEnd && x.AStart == y.AStart);

            foreach (var h1 in hm)
            {
                var w1 = Widen(h1, km);
                foreach (var h2 in hi)
                {
                    if (Grouped(h1, h2)) continue; // settled (or refused) together in Settle
                    var w2 = Widen(h2, ki);
                    var overlap = Math.Max(w1.Lo, w2.Lo) < Math.Min(w1.Hi, w2.Hi)
                                  || (w1.Lo == w1.Hi && w2.Lo < w1.Lo && w1.Lo < w2.Hi)
                                  || (w2.Lo == w2.Hi && w1.Lo < w2.Lo && w2.Lo < w1.Hi);
                    var touch = w1.Hi == w2.Lo || w2.Hi == w1.Lo;
                    if (overlap || (touch && (w1.Slides || w2.Slides))) throw Ambiguous();
                }
            }
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

    private static XDocument Load(OpenXmlPart part) => DocxMeaning.Load(part);

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

    private static string NotesText(WordprocessingDocument doc) =>
        (doc.MainDocumentPart!.FootnotesPart is { } f ? Load(f).Root!.Value : "") + "\u0000" +
        (doc.MainDocumentPart!.EndnotesPart is { } e ? Load(e).Root!.Value : "");

    // Every section's headers and footers AS DISPLAYED, compared as meaning (text, formatting, image
    // targets). A first-page part only shows with titlePg, an even-page part only with
    // evenAndOddHeaders; otherwise the default shows. That is what makes LibreOffice's extra parts (an
    // empty "even", a "first" copied from the default) equal to a document that never had them.
    private static string Headers(WordprocessingDocument doc)
    {
        var main = doc.MainDocumentPart!;
        var settings = main.DocumentSettingsPart is { } sp ? Load(sp).Root : null;
        var evenOdd = On(settings?.Element(W + "evenAndOddHeaders"));
        var sb = new System.Text.StringBuilder();
        foreach (var sect in Load(main).Descendants(W + "sectPr"))
        {
            var titlePg = On(sect.Element(W + "titlePg"));
            foreach (var kind in new[] { "header", "footer" })
            {
                var byType = sect.Elements(W + kind + "Reference")
                    .GroupBy(r => (string?)r.Attribute(W + "type") ?? "default")
                    .ToDictionary(g => g.Key, g => Content(main, (string?)g.First().Attribute(R + "id")));
                var def = byType.GetValueOrDefault("default", "");
                var first = titlePg ? byType.GetValueOrDefault("first", def) : def;
                var even = evenOdd ? byType.GetValueOrDefault("even", def) : def;
                foreach (var (type, content) in new[] { ("default", def), ("first", first), ("even", even) })
                    if (content.Length > 0) sb.Append(kind).Append(':').Append(type).Append('=').Append(content).Append('\u0000');
            }
        }
        return sb.ToString();
    }

    private static bool On(XElement? toggle) =>
        toggle is not null && (string?)toggle.Attribute(W + "val") is null or "1" or "true" or "on";

    private static string Content(MainDocumentPart main, string? id)
    {
        var part = main.Parts.Where(p => p.RelationshipId == id).Select(p => p.OpenXmlPart).FirstOrDefault();
        if (part is null) return "";
        var meaning = new DocxMeaning(main, part);
        return string.Concat(Load(part).Root!.Elements().Select(meaning.Of));
    }

    private const int LabelLength = 60;

    private static string Label(XElement block)
    {
        var text = string.Concat(block.Descendants(W + "t").Select(t => t.Value)).Trim();
        return text.Length <= LabelLength ? text : text[..LabelLength].TrimEnd() + "…";
    }
}
