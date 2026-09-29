using System.IO.Compression;
using System.Xml.Linq;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Tests.Fixtures;
using static EasyDocs.Api.Tests.Fixtures.DocxFixtures;

// Pure: the block-level fold behind merge-into-main. Each case is ancestor / main / incoming -> the
// body the merge compares main against. The rule under test: the fold is either right, or it refuses
// (NotSupportedException -> 409 "Merge unavailable"). It never silently loses, duplicates or reverts.
public class ThreeWayMergeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static ThreeWayMerge.Result Apply(byte[] a, byte[] m, byte[] i) => ThreeWayMerge.Apply(a, m, i);

    private static string[] Fold(string[] ancestor, string[] main, string[] incoming) =>
        Paragraphs(Apply(Build(ancestor), Build(main), Build(incoming)).Docx);

    private static XElement Body(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(s).Root!.Element(W + "body")!;
    }

    private static string[] Paragraphs(byte[] docx) =>
        [.. Body(docx).Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))];

    private static void Refused(byte[] a, byte[] m, byte[] i) =>
        Assert.Throws<NotSupportedException>(() => Apply(a, m, i));

    [Fact]
    public void Edits_to_different_paragraphs_both_survive_even_when_adjacent() =>
        Assert.Equal(["a", "B-main", "C-inc", "d"],
            Fold(["a", "b", "c", "d"], ["a", "B-main", "c", "d"], ["a", "b", "C-inc", "d"]));

    [Fact]
    public void A_paragraph_main_deleted_and_incoming_left_alone_stays_deleted() =>
        Assert.Equal(["a", "c", "D-inc"],
            Fold(["a", "b", "c", "d"], ["a", "c", "d"], ["a", "b", "c", "D-inc"]));

    [Fact]
    public void A_paragraph_main_added_survives_and_incoming_additions_land_beside_it() =>
        Assert.Equal(["a", "main-new", "b", "inc-new"],
            Fold(["a", "b"], ["a", "main-new", "b"], ["a", "b", "inc-new"]));

    [Fact]
    public void Both_inserting_at_one_point_keeps_both_and_shared_blocks_once() =>
        Assert.Equal(["a", "shared", "inc-only"],
            Fold(["a"], ["a", "shared"], ["a", "shared", "inc-only"]));

    [Fact]
    public void A_paragraph_conflict_proposes_the_incoming_paragraph_and_names_it()
    {
        var r = Apply(Build("a", "b", "c"), Build("a", "B-main", "c"), Build("a", "B-inc", "c"));
        Assert.Equal(["a", "B-inc", "c"], Paragraphs(r.Docx));
        Assert.Equal([new ThreeWayMerge.Overlap(1, "b")], r.Overlaps);
    }

    [Fact]
    public void Main_is_returned_untouched_when_incoming_changed_nothing()
    {
        var r = Apply(Build("a", "b"), Build("a", "B-main"), Build("a", "b"));
        Assert.Equal(["a", "B-main"], Paragraphs(r.Docx));
        Assert.Empty(r.Overlaps);
    }

    // --- refusals: each of these used to come out silently wrong ---

    [Fact]
    public void An_incoming_image_swap_is_refused_not_dropped()
    {
        byte[] png1 = [1, 2, 3], png2 = [4, 5, 6];
        Refused(Rich(P("x"), Image("logo", png1)), Rich(P("X-main"), Image("logo", png1)), Rich(P("x"), Image("logo", png2)));
    }

    [Fact]
    public void An_incoming_hyperlink_url_change_is_refused_not_dropped() =>
        Refused(Rich(P("x"), Link("site", "https://a.example", "rL")),
                Rich(P("X-main"), Link("site", "https://a.example", "rL")),
                Rich(P("x"), Link("site", "https://b.example", "rL")));

    [Fact]
    public void An_incoming_paragraph_with_a_link_main_does_not_have_is_refused() =>
        Refused(Rich(P("x")), Rich(P("X-main")), Rich(P("x"), Link("new", "https://b.example", "rNew")));

    [Fact]
    public void A_renumbered_relationship_with_the_same_target_is_noise()
    {
        var r = Apply(Rich(P("x"), Link("site", "https://a.example", "rA")),
                      Rich(P("X-main"), Link("site", "https://a.example", "rA")),
                      Rich(P("x"), Link("site", "https://a.example", "rB")));
        Assert.Equal(["X-main", "site"], Paragraphs(r.Docx));
        Assert.Equal("rA", Body(r.Docx).Descendants(W + "hyperlink").Single().Attribute(R + "id")!.Value);
    }

    [Fact]
    public void An_incoming_footnote_edit_is_refused() =>
        Refused(Rich(Footnoted("x", "note")), Rich(Footnoted("X-main", "note")), Rich(Footnoted("x", "note, amended")));

    [Fact]
    public void An_incoming_header_edit_is_refused() =>
        Refused(Rich("Header", P("x")), Rich("Header", P("X-main")), Rich("Header, amended", P("x")));

    [Fact]
    public void Edits_to_different_cells_of_one_table_are_refused() =>
        Refused(Rich(Table(["a", "b"], ["c", "d"])), Rich(Table(["A-main", "b"], ["c", "d"])), Rich(Table(["a", "b"], ["c", "D-inc"])));

    [Fact]
    public void An_edited_table_main_left_alone_lands()
    {
        var r = Apply(Rich(P("x"), Table(["a", "b"])), Rich(P("X-main"), Table(["a", "b"])), Rich(P("x"), Table(["a", "B-inc"])));
        Assert.Equal(["X-main", "a", "B-inc"], Paragraphs(r.Docx));
    }

    // Alice deletes one of three identical lines; Bob fills in the third. Which copy Alice deleted is
    // ambiguous, and guessing wrong resurrects her deletion.
    [Fact]
    public void An_ambiguous_deletion_among_identical_paragraphs_is_refused() =>
        Assert.Throws<NotSupportedException>(() => Fold(
            ["Intro.", "Initials: ____", "Initials: ____", "Initials: ____", "End."],
            ["Intro.", "Initials: ____", "Initials: ____", "End."],
            ["Intro.", "Initials: ____", "Initials: ____", "Initials: RZ", "End."]));

    // Main moves a paragraph, incoming edits it where it was: a block fold would keep both copies.
    [Fact]
    public void A_paragraph_moved_by_one_side_and_edited_by_the_other_is_refused() =>
        Assert.Throws<NotSupportedException>(() => Fold(
            ["alpha", "beta", "gamma"], ["beta", "gamma", "alpha"], ["alpha, amended", "beta", "gamma"]));

    [Fact]
    public void A_changed_region_too_large_to_align_is_refused()
    {
        var n = 2100;
        Assert.Throws<NotSupportedException>(() => Fold(
            [.. Enumerable.Range(0, n).Select(k => $"a{k}")],
            [.. Enumerable.Range(0, n).Select(k => $"m{k}")],
            [.. Enumerable.Range(0, n).Select(k => $"i{k}")]));
    }

    // Random non-overlapping edits over a vocabulary full of duplicates and blanks: whatever the fold
    // does not refuse must lose, duplicate and resurrect nothing. Among identical paragraphs WHICH copy
    // a side deleted is genuinely ambiguous, so the oracle checks content, not one arbitrary order:
    // every new paragraph exactly once, each side's new paragraphs in that side's order, and every
    // ancestor wording exactly as many times as both edit sets applied together leave it.
    [Fact]
    public void Fuzz_non_overlapping_edits_lose_duplicate_and_resurrect_nothing()
    {
        string[] vocab = ["", "", "Sig: ___", "x", "y"];
        var rng = new Random(20260929);
        var refused = 0;
        for (var run = 0; run < 1000; run++)
        {
            var anc = Enumerable.Range(0, rng.Next(2, 9)).Select(_ => vocab[rng.Next(vocab.Length)]).ToList();
            List<string> main = [], inc = [], expected = [];
            for (var k = 0; k < anc.Count; k++)
            {
                var owner = rng.Next(3); // 0 nobody, 1 main, 2 incoming
                var op = rng.Next(3);    // 0 delete, 1 replace, 2 insert after
                string Tok(string side) => $"{side}{run}.{k}";
                void Keep(List<string> side) => side.Add(anc[k]);
                if (owner == 0) { Keep(main); Keep(inc); expected.Add(anc[k]); continue; }
                var (mine, other, tag) = owner == 1 ? (main, inc, "M") : (inc, main, "I");
                Keep(other);
                if (op == 1) { mine.Add(Tok(tag)); expected.Add(Tok(tag)); }
                if (op == 2) { mine.Add(anc[k]); mine.Add(Tok(tag)); expected.Add(anc[k]); expected.Add(Tok(tag)); }
            }
            string[] got;
            try { got = Fold([.. anc], [.. main], [.. inc]); }
            catch (NotSupportedException) { refused++; continue; }

            var why = $"ancestor [{string.Join("|", anc)}] main [{string.Join("|", main)}] "
                + $"incoming [{string.Join("|", inc)}] -> [{string.Join("|", got)}], expected [{string.Join("|", expected)}]";
            static bool New(string p) => p.StartsWith('M') || p.StartsWith('I');
            Assert.True(expected.Where(p => p.StartsWith('M')).SequenceEqual(got.Where(p => p.StartsWith('M'))), why);
            Assert.True(expected.Where(p => p.StartsWith('I')).SequenceEqual(got.Where(p => p.StartsWith('I'))), why);
            Assert.True(expected.Where(p => !New(p)).Order().SequenceEqual(got.Where(p => !New(p)).Order()), why);
        }
        Assert.True(refused < 500, $"refused {refused}/1000 — the fold should settle most of these");
    }
}
