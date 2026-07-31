namespace EasyDocs.Api.Storage;

/// <summary>
/// What a stored blob actually IS, for the two things that have to tell the truth about it: the
/// Content-Type of a download and the R8 extension of its filename (spec §5.3).
///
/// Derived from the BYTES, never from the client's multipart Content-Type or filename — both are
/// untrusted input (spec §10.3), and echoing a client-supplied content type into a response header is a
/// content-sniffing / XSS vector. The result is always one of the three server-side constants below, so
/// nothing a caller sends can reach a header.
///
/// easydocs is a .docx product, so docx stays the DEFAULT: only a positively recognised non-docx
/// signature deviates from it. That keeps every already-stored blob labelled exactly as it is today and
/// fixes only the ones that were mislabelled.
/// </summary>
public static class BlobMime
{
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Doc = "application/msword";
    public const string Pdf = "application/pdf";

    /// <summary>Extensions R8 can append — and therefore the ones stripped off a document name that
    /// already carries one (see <see cref="Versioning.Numbering.DownloadFileName"/>).</summary>
    public static readonly string[] KnownExtensions = [".docx", ".doc", ".pdf"];

    private const int HeadBytes = 8;
    private static readonly byte[] Ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // ponytail: magic bytes only, three outcomes. Ceiling: an .xlsx (also a zip) is labelled docx and a
    // legacy .xls (also OLE2) is labelled doc — exactly as they are today, and neither is a thing this
    // product stores. Upgrade path if it ever matters: read [Content_Types].xml out of the zip.
    public static (string Mime, string Ext) Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF-"u8)) return (Pdf, "pdf");
        if (head.StartsWith(Ole2Signature)) return (Doc, "doc"); // OLE2 compound file = legacy .doc
        return (Docx, "docx");
    }

    /// <summary>
    /// Sniffs a stored blob. Deliberately re-reads the head at serve time instead of trusting
    /// Blobs.Mime: the bytes cannot be stale, which is what makes the rows written before this fix
    /// (labelled docx whatever they were) download correctly with no backfill migration.
    /// </summary>
    public static async Task<(string Mime, string Ext)> SniffAsync(IBlobStore blobs, string sha256, CancellationToken ct)
    {
        var head = new byte[HeadBytes];
        await using var s = await blobs.OpenReadAsync(sha256, ct);
        var read = await s.ReadAtLeastAsync(head, HeadBytes, throwOnEndOfStream: false, ct);
        return Sniff(head.AsSpan(0, read));
    }
}
