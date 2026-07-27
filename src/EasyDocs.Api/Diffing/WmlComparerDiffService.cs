using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Clippit;
using Clippit.Word;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Diffing;

// Concrete (no interface): numeric summary + on-demand redline over Clippit.WmlComparer.
// EVERY Compare call is try/catch-guarded — a malformed/uncomparable docx degrades to
// Available=false and NEVER throws to the caller (spec §7). Results are cached in version_diffs
// keyed by (from_sha, to_sha).
public sealed class WmlComparerDiffService(IBlobStore blobs, EasyDocsDbContext db, ILogger<WmlComparerDiffService> log)
{
    public record DiffSummary(bool Available, int Insertions, int Deletions, int Moves, int FormatChanges);
    public record DiffRender(bool Available, string? Html);

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public async Task<DiffSummary> SummaryAsync(string fromSha, string toSha, CancellationToken ct)
    {
        try
        {
            var compared = await CompareAsync(fromSha, toSha, ct);
            var revisions = WmlComparer.GetRevisions(compared, new WmlComparerSettings());
            var insertions = revisions.Count(r => r.RevisionType == WmlComparer.WmlComparerRevisionType.Inserted);
            var deletions = revisions.Count(r => r.RevisionType == WmlComparer.WmlComparerRevisionType.Deleted);

            // ponytail: WmlComparer.GetRevisions only classifies Inserted/Deleted, so Moves/FormatChanges
            // stay 0 for M1 (upgrade path: derive from w:moveFrom/w:moveTo and w:rPrChange when needed).
            var row = await UpsertAsync(fromSha, toSha, ct);
            row.Insertions = insertions;
            row.Deletions = deletions;
            row.Moves = 0;
            row.FormatChanges = 0;
            await db.SaveChangesAsync(ct);

            return new DiffSummary(true, insertions, deletions, 0, 0);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WmlComparer summary failed for {From}->{To}; degrading to unavailable", fromSha, toSha);
            return new DiffSummary(false, 0, 0, 0, 0);
        }
    }

    public async Task<DiffRender> RedlineHtmlAsync(string fromSha, string toSha, CancellationToken ct)
    {
        var existing = await db.VersionDiffs
            .FirstOrDefaultAsync(x => x.FromSha256 == fromSha && x.ToSha256 == toSha, ct);
        if (existing?.HtmlBlobSha256 is { } cachedSha)
            return new DiffRender(true, await ReadTextAsync(cachedSha, ct));

        try
        {
            var compared = await CompareAsync(fromSha, toSha, ct);
            var html = RenderHtml(compared);

            var htmlBlob = await blobs.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(html)), ct);
            var docxBlob = await blobs.PutAsync(new MemoryStream(compared.DocumentByteArray), ct);

            var row = existing ?? await UpsertAsync(fromSha, toSha, ct);
            row.HtmlBlobSha256 = htmlBlob.Sha256;
            row.RedlineBlobSha256 = docxBlob.Sha256;
            await db.SaveChangesAsync(ct);

            return new DiffRender(true, html);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "WmlComparer redline failed for {From}->{To}; degrading to unavailable", fromSha, toSha);
            return new DiffRender(false, null);
        }
    }

    private async Task<WmlDocument> CompareAsync(string fromSha, string toSha, CancellationToken ct)
    {
        var source = new WmlDocument("from.docx", await ReadBytesAsync(fromSha, ct));
        var target = new WmlDocument("to.docx", await ReadBytesAsync(toSha, ct));
        return WmlComparer.Compare(source, target, new WmlComparerSettings());
    }

    // Minimal, dependency-free redline HTML from the compared docx: walk word/document.xml and mark
    // w:ins runs as <ins> and w:del (w:delText) as <del>. Clippit's WmlToHtmlConverter throws on the
    // compared package (missing footnote/numbering parts), so this stdlib rendering is the M1 render.
    private static string RenderHtml(WmlDocument compared)
    {
        using var zip = new ZipArchive(new MemoryStream(compared.DocumentByteArray), ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("compared docx has no word/document.xml");
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);

        var sb = new StringBuilder("<article class=\"redline\">");
        foreach (var p in doc.Descendants(W + "p"))
        {
            sb.Append("<p>");
            foreach (var t in p.Descendants(W + "t"))
                if (t.Ancestors(W + "ins").Any())
                    sb.Append("<ins>").Append(Escape(t.Value)).Append("</ins>");
                else
                    sb.Append(Escape(t.Value));
            foreach (var d in p.Descendants(W + "delText"))
                sb.Append("<del>").Append(Escape(d.Value)).Append("</del>");
            sb.Append("</p>");
        }
        return sb.Append("</article>").ToString();
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task<VersionDiff> UpsertAsync(string fromSha, string toSha, CancellationToken ct)
    {
        var row = await db.VersionDiffs.FirstOrDefaultAsync(x => x.FromSha256 == fromSha && x.ToSha256 == toSha, ct);
        if (row is null)
        {
            row = new VersionDiff { FromSha256 = fromSha, ToSha256 = toSha, CreatedAt = DateTimeOffset.UtcNow };
            db.Add(row);
        }
        return row;
    }

    private async Task<byte[]> ReadBytesAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private async Task<string> ReadTextAsync(string sha, CancellationToken ct)
    {
        await using var s = await blobs.OpenReadAsync(sha, ct);
        using var r = new StreamReader(s, Encoding.UTF8);
        return await r.ReadToEndAsync(ct);
    }
}
