using Clippit;
using Clippit.Word;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Tests.Fixtures;

public class ThreeWayOverlapTests
{
    // Compare two fixture documents the same way the product does, so the walker is exercised against
    // real WmlComparer output rather than hand-written XML that might not match what Clippit emits.
    private static WmlDocument Compare(byte[] from, byte[] to) =>
        WmlComparer.Compare(new WmlDocument("f.docx", from), new WmlDocument("t.docx", to),
            new WmlComparerSettings());

    [Fact]
    public void Reports_a_paragraph_both_sides_changed()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "Bravo MAIN", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo BRANCH", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Equal(1, overlaps[0].Ordinal);          // zero-based: Alpha=0, Bravo=1
        Assert.Contains("Bravo", overlaps[0].Text);    // labelled from BASE text, not either edit
    }

    [Fact]
    public void Ignores_a_paragraph_only_one_side_changed()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha MAIN", "Bravo", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo", "Charlie BRANCH"));

        Assert.Empty(ThreeWayOverlap.Find(main, incoming));
    }

    // THE regression this design exists to prevent. Main inserts a paragraph ABOVE the shared edit, so
    // naive index alignment would shift main's ordinals by one and miss the overlap entirely.
    [Fact]
    public void An_insertion_on_one_side_does_not_shift_the_other_sides_ordinals()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "INSERTED", "Bravo MAIN", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Bravo BRANCH", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Equal(1, overlaps[0].Ordinal);
        Assert.Contains("Bravo", overlaps[0].Text);
    }

    // The case that rejected text-matching as the anchor: real leases repeat "Intentionally omitted."
    // many times, and matching on text would call every repetition an overlap.
    [Fact]
    public void Repeated_identical_paragraphs_do_not_produce_false_positives()
    {
        var b = DocxFixtures.Build("Intentionally omitted.", "Bravo", "Intentionally omitted.");
        var main = Compare(b, DocxFixtures.Build("Intentionally omitted. MAIN", "Bravo", "Intentionally omitted."));
        var incoming = Compare(b, DocxFixtures.Build("Intentionally omitted.", "Bravo", "Intentionally omitted. BRANCH"));

        // Ordinal 0 and ordinal 2 are different paragraphs that happen to read the same. One side
        // touched each. Neither is an overlap.
        Assert.Empty(ThreeWayOverlap.Find(main, incoming));
    }

    [Fact]
    public void A_paragraph_deleted_by_both_sides_is_an_overlap()
    {
        var b = DocxFixtures.Build("Alpha", "Bravo", "Charlie");
        var main = Compare(b, DocxFixtures.Build("Alpha", "Charlie"));
        var incoming = Compare(b, DocxFixtures.Build("Alpha", "Charlie"));

        var overlaps = ThreeWayOverlap.Find(main, incoming);

        Assert.Single(overlaps);
        Assert.Contains("Bravo", overlaps[0].Text);   // labelled from base content it no longer has
    }

    [Fact]
    public void Long_paragraphs_are_truncated_with_an_ellipsis()
    {
        var long1 = new string('x', 80) + " TAIL";
        var b = DocxFixtures.Build(long1);
        var main = Compare(b, DocxFixtures.Build(long1 + " MAIN"));
        var incoming = Compare(b, DocxFixtures.Build(long1 + " BRANCH"));

        var text = ThreeWayOverlap.Find(main, incoming)[0].Text;

        Assert.EndsWith("…", text);
        Assert.DoesNotContain("TAIL", text);
        Assert.True(text.Length <= 61);   // 60 chars + the ellipsis
    }
}
