using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Sharing;

// Share links + public tokenized viewer (E10). Only the token HASH is stored; the raw token is
// returned once. The public /s/{token} route is anonymous and audited (spec §11).
public static class ShareEndpoints
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public record CreateRequest(DateTimeOffset? ExpiresAt);

    public static void MapShareEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").WithTags("Sharing");
        g.MapPost("/api/v1/versions/{vid:guid}/share-links", Create).RequireAuthorization();
        g.MapDelete("/api/v1/share-links/{id:guid}", Revoke).RequireAuthorization();

        // PUBLIC — no RequireAuthorization. Mapped in Program.cs before the SPA fallback, like /wopi.
        // Rate-limited per client because the token IS the capability: unthrottled these are a
        // token-enumeration oracle and a bandwidth amplifier (spec §11, see RateLimits).
        // Separate policies on purpose: the download streams the whole file, so its cap is the egress
        // cap and has no business being loosened to whatever page views need.
        g.MapGet("/s/{token}", PublicView).RequireRateLimiting(RateLimits.AnonShare);
        g.MapGet("/s/{token}/download", PublicDownload).RequireRateLimiting(RateLimits.AnonDownload);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Viewer+ (any member of the version's document) may share.
    private static async Task<IResult> Create(Guid vid, CreateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (result, _) = await DocumentAuthorization.ResolveAsync(
            db, CurrentUser.OrgId(ctx.User), CurrentUser.UserId(ctx.User), version.DocumentId, ctx.RequestAborted);
        switch (result)
        {
            case AccessResult.NotFound: return Problem.Of(404, "Not found", "Document not found.");
            case AccessResult.Forbidden: return Problem.Of(403, "Forbidden", "You do not have access to this document.");
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)); // 128-bit, url-safe
        var link = new ShareLink
        {
            VersionId = vid,
            TokenHash = HashToken(token),
            CreatedBy = CurrentUser.UserId(ctx.User),
            ExpiresAt = req.ExpiresAt,
            ViewCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(link);
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), version.DocumentId, CurrentUser.UserId(ctx.User),
            "share_link.created", "version", vid.ToString(), new { expiresAt = req.ExpiresAt }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Created($"/s/{token}", new { token, url = $"/s/{token}" });
    }

    // Creator or an Editor+ of the document may revoke.
    private static async Task<IResult> Revoke(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var link = await db.ShareLinks.FirstOrDefaultAsync(x => x.Id == id, ctx.RequestAborted);
        if (link is null) return Problem.Of(404, "Not found", "Share link not found.");

        var docId = await db.Versions.Where(v => v.Id == link.VersionId).Select(v => v.DocumentId).FirstAsync(ctx.RequestAborted);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, CurrentUser.OrgId(ctx.User), userId, docId, ctx.RequestAborted);
        switch (result)
        {
            case AccessResult.NotFound: return Problem.Of(404, "Not found", "Document not found.");
            case AccessResult.Forbidden: return Problem.Of(403, "Forbidden", "You do not have access to this document.");
        }
        if (link.CreatedBy != userId && !DocumentAuthorization.CanEdit(role!.Value))
            return Problem.Of(403, "Forbidden", "Only the creator or an editor may revoke this link.");

        link.RevokedAt ??= DateTimeOffset.UtcNow;
        db.Add(Audit.Event(CurrentUser.OrgId(ctx.User), docId, userId, "share_link.revoked",
            "share_link", link.Id.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }

    // Resolve a live (non-revoked, non-expired) link by token, or null. No info leak — callers 404.
    private static async Task<ShareLink?> ResolveLiveAsync(EasyDocsDbContext db, string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        return await db.ShareLinks.FirstOrDefaultAsync(
            x => x.TokenHash == hash && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now), ct);
    }

    // PUBLIC viewer: version metadata + a download link. Increments the view count and audits the read.
    private static async Task<IResult> PublicView(string token, HttpContext ctx, EasyDocsDbContext db, IWebHostEnvironment env)
    {
        // One URL, two representations — so it MUST tell caches, or the browser replays the wrong one.
        // Without these the shipped image failed for real: the HTML branch below is a static file, so it
        // carries Last-Modified and no Cache-Control, Chromium heuristically cached it, and the SPA's
        // Accept: application/json fetch of this same URL got the cached shell back (same Content-Length,
        // same Date) instead of the JSON. `no-store` is the right answer independently: this GET counts a
        // view and writes an audit row, so a cached response silently loses both. Vite's dev server sends
        // no-cache on index.html, which is exactly why dev never saw this.
        ctx.Response.Headers.Vary = "Accept";
        ctx.Response.Headers.CacheControl = "no-store";

        // A browser navigating here wants the landing page; the SPA then re-requests this same URL with
        // Accept: application/json. Returned before any DB work so the shell hit neither audits nor
        // counts a view, and so an unknown token is not distinguishable from a live one.
        if (ctx.Request.GetTypedHeaders().Accept
                .Any(a => a.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            var shell = env.WebRootFileProvider.GetFileInfo("index.html");
            // ponytail: falls through to JSON when the SPA has not been built (dotnet run without a
            // prior `npm run build`). Drop the fallback once the build always produces wwwroot.
            if (shell.Exists && shell.PhysicalPath is not null)
                return Results.File(shell.PhysicalPath, "text/html");
        }

        var link = await ResolveLiveAsync(db, token, ctx.RequestAborted);
        if (link is null) return Problem.Of(404, "Not found", "Link not found.");

        var version = await db.Versions.FirstAsync(v => v.Id == link.VersionId, ctx.RequestAborted);
        var doc = await db.Documents.FirstAsync(d => d.Id == version.DocumentId, ctx.RequestAborted);

        link.ViewCount++;
        db.Add(new AuditEvent
        {
            OrgId = doc.OrgId,
            DocumentId = doc.Id,
            ActorUserId = null, // anonymous recipient
            Action = "share_link.viewed",
            TargetType = "version",
            TargetId = version.Id.ToString(),
            Metadata = JsonSerializer.Serialize(new { ip = ctx.Connection.RemoteIpAddress?.ToString(), shareLinkId = link.Id }),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ctx.RequestAborted);

        return Results.Ok(new
        {
            documentName = doc.Name,
            version = $"{version.Major}.{version.Minor}.{version.Revision}",
            downloadUrl = $"/s/{token}/download",
        });
    }

    // PUBLIC download: stream the docx blob with the R8 filename.
    private static async Task<IResult> PublicDownload(string token, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs)
    {
        var link = await ResolveLiveAsync(db, token, ctx.RequestAborted);
        if (link is null) return Problem.Of(404, "Not found", "Link not found.");

        var version = await db.Versions.FirstAsync(v => v.Id == link.VersionId, ctx.RequestAborted);
        var doc = await db.Documents.FirstAsync(d => d.Id == version.DocumentId, ctx.RequestAborted);
        var slug = await db.Organizations.Where(o => o.Id == doc.OrgId).Select(o => o.Slug).FirstAsync(ctx.RequestAborted);

        var name = Numbering.DownloadFileName(slug, doc.Name, (version.Major, version.Minor, version.Revision), "docx");
        return Results.Stream(await blobs.OpenReadAsync(version.BlobSha256, ctx.RequestAborted), DocxMime, name);
    }
}
