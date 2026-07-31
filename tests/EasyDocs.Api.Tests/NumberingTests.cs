using EasyDocs.Api.Versioning;

public class NumberingTests
{
    [Fact] public void R1_first_draft_is_0_0_1() => Assert.Equal((0, 0, 1), Numbering.NextDraft((0, 0, 0)));
    [Fact] public void R2_draft_increments_rev() => Assert.Equal((0, 0, 8), Numbering.NextDraft((0, 0, 7)));
    [Fact] public void R3_publish_minor_0_0_7_to_0_1_0() => Assert.Equal((0, 1, 0), Numbering.PublishMinor((0, 0, 7)));
    [Fact] public void R4_publish_major_0_0_7_to_1_0_0() => Assert.Equal((1, 0, 0), Numbering.PublishMajor((0, 0, 7)));
    [Fact] public void R5_manual_allows_zeroes() => Assert.Equal((0, 0, 0), Numbering.Manual(0, 0, 0));
    [Fact] public void R5_manual_rejects_negatives() => Assert.Throws<ArgumentOutOfRangeException>(() => Numbering.Manual(-1, 0, 0));
    [Fact] public void R6_draft_after_minor_publish_continues_from_counter() => Assert.Equal((0, 1, 1), Numbering.NextDraft(Numbering.PublishMinor((0, 0, 7))));
    [Fact] public void R8_download_filename() => Assert.Equal("aces__Master_Lease-v0.1.0.docx", Numbering.DownloadFileName("aces", "Master Lease", (0, 1, 0), "docx"));
    // R8: real corpus names carry the original file's extension. It must not survive into the middle of
    // the download name ("Lease.pdf-v0.0.1.pdf") — the extension R8 appends is the only one.
    [Fact] public void R8_does_not_repeat_an_extension_already_in_the_name() => Assert.Equal("aces__Lease-v0.0.1.pdf", Numbering.DownloadFileName("aces", "Lease.pdf", (0, 0, 1), "pdf"));
    [Fact] public void R8_strips_a_docx_extension_from_the_name_too() => Assert.Equal("aces__Lease-v0.0.1.docx", Numbering.DownloadFileName("aces", "Lease.docx", (0, 0, 1), "docx"));
    // Only a known document extension is stripped: a dot in the middle of a name is part of the name.
    [Fact] public void R8_keeps_dots_that_are_not_a_known_extension() => Assert.Equal("aces__Suite_4.2_Lease-v0.0.1.docx", Numbering.DownloadFileName("aces", "Suite 4.2 Lease", (0, 0, 1), "docx"));
}
