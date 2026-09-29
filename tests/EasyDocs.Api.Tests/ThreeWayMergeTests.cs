using System.IO.Compression;
using System.Xml.Linq;
using EasyDocs.Api.Merging;
using EasyDocs.Api.Tests.Fixtures;

// Pure: the block-level fold behind merge-into-main. Each case is ancestor / main / incoming -> the
// body the merge compares main against.
public class ThreeWayMergeTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string[] Fold(string[] ancestor, string[] main, string[] incoming) =>
        Paragraphs(ThreeWayMerge.Apply(DocxFixtures.Build(ancestor), DocxFixtures.Build(main), DocxFixtures.Build(incoming)));

    private static string[] Paragraphs(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return [.. XDocument.Load(s).Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))];
    }

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
    public void A_genuine_conflict_proposes_the_incoming_paragraph() =>
        Assert.Equal(["a", "B-inc", "c"],
            Fold(["a", "b", "c"], ["a", "B-main", "c"], ["a", "B-inc", "c"]));

    [Fact]
    public void Main_is_returned_untouched_when_incoming_changed_nothing() =>
        Assert.Equal(["a", "B-main"], Fold(["a", "b"], ["a", "B-main"], ["a", "b"]));
}
