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

    // Pandoc writes bookmarks BETWEEN paragraphs; LibreOffice moves them inside on save. Neither is a
    // block, so a save must not read as both sides deleting one — and main's anchor must survive.
    [Fact]
    public void Body_level_bookmarks_moved_into_paragraphs_by_a_save_are_not_edits()
    {
        var r = Apply(
            Rich(BookmarkStart("intro", 0), P("Alpha"), BookmarkEnd(0), P("Bravo"), P("Charlie"), P("Delta")),
            Rich(Bookmarked("Alpha EDITED", "intro", 0), P("Bravo"), P("Charlie"), P("Delta")),
            Rich(Bookmarked("Alpha", "intro", 0), P("Bravo"), P("Charlie"), P("Delta EDITED")));
        Assert.Equal(["Alpha EDITED", "Bravo", "Charlie", "Delta EDITED"], Paragraphs(r.Docx));
        Assert.Empty(r.Overlaps);
        Assert.Equal("intro", (string?)Body(r.Docx).Descendants(W + "bookmarkStart").Single().Attribute(W + "name"));
    }

    [Fact]
    public void Main_body_level_bookmarks_are_kept_when_nobody_saved_through_the_editor()
    {
        var anc = Rich(BookmarkStart("intro", 0), P("Alpha"), BookmarkEnd(0), P("Bravo"));
        var r = Apply(anc, Rich(BookmarkStart("intro", 0), P("Alpha EDITED"), BookmarkEnd(0), P("Bravo")),
            Rich(BookmarkStart("intro", 0), P("Alpha"), BookmarkEnd(0), P("Bravo EDITED")));
        Assert.Equal(["Alpha EDITED", "Bravo EDITED"], Paragraphs(r.Docx));
        var body = Body(r.Docx);
        Assert.Single(body.Descendants(W + "bookmarkStart"));
        Assert.Single(body.Descendants(W + "bookmarkEnd"));
    }

    // Main indents a paragraph (formatting the meaning comparison does not see); incoming rewords it.
    // Taking incoming's copy wholesale used to drop main's indent without a trace.
    [Fact]
    public void Main_paragraph_layout_survives_an_incoming_reword()
    {
        var r = Apply(Rich(P("Rent is due monthly."), P("b")), Rich(Indented("Rent is due monthly.", 720), P("b")),
            Rich(P("Rent is due quarterly."), P("b")));
        var first = Body(r.Docx).Elements(W + "p").First();
        Assert.Equal("Rent is due quarterly.", string.Concat(first.Descendants(W + "t").Select(t => t.Value)));
        Assert.Equal("720", (string?)first.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "left"));
    }

    [Fact]
    public void Main_column_widths_survive_an_incoming_cell_edit()
    {
        var r = Apply(Rich(GridTable([2000, 2000], ["a", "b"])), Rich(GridTable([3000, 1000], ["a", "b"])),
            Rich(GridTable([2000, 2000], ["a", "B-inc"])));
        Assert.Equal(["a", "B-inc"], Paragraphs(r.Docx));
        Assert.Equal(["3000", "1000"], Body(r.Docx).Descendants(W + "gridCol").Select(g => (string)g.Attribute(W + "w")!));
    }

    // ...but carrying main's layout must not revert the incoming side's OWN layout change: main's copy
    // of a block it left alone is the ancestor's, so taking it wholesale would undo incoming's indent.
    [Fact]
    public void Incoming_indent_survives_when_incoming_rewords_and_indents()
    {
        var r = Apply(Rich(P("Late fees are due."), P("b")), Rich(P("Late fees are due."), P("B-main")),
            Rich(Indented("Late charges are due.", 1440), P("b")));
        var first = Body(r.Docx).Elements(W + "p").First();
        Assert.Equal("Late charges are due.", string.Concat(first.Descendants(W + "t").Select(t => t.Value)));
        Assert.Equal("1440", (string?)first.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "left"));
        Assert.Equal(["Late charges are due.", "B-main"], Paragraphs(r.Docx));
    }

    [Fact]
    public void Incoming_table_borders_and_widths_survive_an_incoming_cell_edit()
    {
        var r = Apply(Rich(StyledTable([3000, 3000], false, ["a", "b"]), P("x")),
            Rich(StyledTable([3000, 3000], false, ["a", "b"]), P("X-main")),
            Rich(StyledTable([5000, 3000], true, ["A-inc", "b"]), P("x")));
        var tbl = Body(r.Docx).Element(W + "tbl")!;
        Assert.NotNull(tbl.Element(W + "tblPr")?.Element(W + "tblBorders"));
        Assert.Equal(["5000", "3000"], tbl.Descendants(W + "gridCol").Select(g => (string)g.Attribute(W + "w")!));
        Assert.Equal("5000", (string?)tbl.Descendants(W + "tcW").First().Attribute(W + "w"));
    }

    // Same header text, different logo: text-only header comparison used to miss this and drop it.
    [Fact]
    public void An_incoming_header_image_change_is_refused()
    {
        byte[] png1 = [1, 2, 3], png2 = [4, 5, 6];
        Refused(Rich("Header", png1, P("x")), Rich("Header", png1, P("X-main")), Rich("Header", png2, P("x")));
    }

    [Fact]
    public void An_unchanged_header_image_is_not_a_change()
    {
        byte[] png = [1, 2, 3];
        Assert.Equal(["X-main", "I-inc"],
            Paragraphs(Apply(Rich("Header", png, P("x"), P("y")), Rich("Header", png, P("X-main"), P("y")), Rich("Header", png, P("x"), P("I-inc"))).Docx));
    }

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

    // Random non-overlapping edits over a vocabulary full of duplicates and blanks: replace, delete,
    // insert-after, and bold (a formatting-only change). Whatever the fold does not refuse must be
    // EXACTLY both edit sets applied to the ancestor — same paragraphs, same order, same bold. Where
    // repeated paragraphs make the answer genuinely ambiguous the fold has to refuse, not pick one.
    // (Bold on an empty paragraph is invisible, so the oracle ignores it — as the fold does.)
    [Fact]
    public void Fuzz_non_overlapping_edits_merge_exactly_or_refuse()
    {
        string[] vocab = ["", "", "Sig: ___", "x", "y", "z"];
        var rng = new Random(20260929);
        var refused = 0;
        for (var run = 0; run < 3000; run++)
        {
            var anc = Enumerable.Range(0, rng.Next(2, 10)).Select(_ => (T: vocab[rng.Next(vocab.Length)], B: false)).ToList();
            List<(string T, bool B)> main = [], inc = [], expected = [];
            for (var k = 0; k < anc.Count; k++)
            {
                var owner = rng.Next(3); // 0 nobody, 1 main, 2 incoming
                var op = rng.Next(4);    // 0 delete, 1 replace, 2 insert after, 3 bold
                var tok = (T: $"{(owner == 1 ? "M" : "I")}{run}.{k}", B: false);
                if (owner == 0) { main.Add(anc[k]); inc.Add(anc[k]); expected.Add(anc[k]); continue; }
                var (mine, other) = owner == 1 ? (main, inc) : (inc, main);
                other.Add(anc[k]);
                if (op == 1) { mine.Add(tok); expected.Add(tok); }
                if (op == 2) { mine.Add(anc[k]); mine.Add(tok); expected.Add(anc[k]); expected.Add(tok); }
                if (op == 3) { mine.Add((anc[k].T, true)); expected.Add((anc[k].T, anc[k].T != "")); }
            }
            ThreeWayMerge.Result r;
            try { r = Apply(Styled(anc), Styled(main), Styled(inc)); }
            catch (NotSupportedException) { refused++; continue; }

            var got = Body(r.Docx).Elements(W + "p").Select(p =>
                (T: string.Concat(p.Descendants(W + "t").Select(t => t.Value)), B: p.Descendants(W + "b").Any())).Select(p => (p.T, B: p.B && p.T != "")).ToList();
            Assert.True(expected.SequenceEqual(got),
                $"ancestor [{string.Join("|", anc)}] main [{string.Join("|", main)}] incoming [{string.Join("|", inc)}] "
                + $"-> [{string.Join("|", got)}], expected [{string.Join("|", expected)}]");
            Assert.Empty(r.Overlaps); // non-overlapping edits: nothing both sides changed
        }
        Assert.True(refused < 900, $"refused {refused}/3000 — the fold should settle most of these");
    }

    private static byte[] Styled(IEnumerable<(string T, bool B)> paragraphs) =>
        Rich([.. paragraphs.Select(p => (Block)(_ => new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
            new DocumentFormat.OpenXml.Wordprocessing.Run(
                p.B ? new DocumentFormat.OpenXml.Wordprocessing.RunProperties(new DocumentFormat.OpenXml.Wordprocessing.Bold()) : null!,
                new DocumentFormat.OpenXml.Wordprocessing.Text(p.T) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }))))]);
}
