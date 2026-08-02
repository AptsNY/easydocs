using System.Security.Cryptography;
using System.Text;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Editing;

// Desktop "Open in Word" (issue #11): a minimal WebDAV class-2 surface under /dav/{token}/{name},
// exactly the verbs Word needs to open and save one file — OPTIONS, PROPFIND (depth 0), HEAD, GET,
// LOCK, UNLOCK, PUT. The token in the path is the same short-TTL edit-session capability WOPI uses,
// because Word will not carry the app's session cookie; the ms-word:ofe|u| URL hands Word the whole
// address, token included.
//
// A PUT is a save: it commits through the SAME single write path as upload and WOPI (spec §5.2),
// with the session's base version — so saving over a stale base branches instead of overwriting,
// identical to two people editing in the browser. Word just becomes a third editor.
public static class WebdavEndpoints
{
    public static void MapWebdavEndpoints(this WebApplication app)
    {
        // The authenticated mint: the UI calls this, then navigates to ms-word:ofe|u|<url>.
        app.MapPost("/api/v1/versions/{vid:guid}/webdav-sessions", Mint)
            .RequireAuthorization().WithTags("Editing");

        var dav = app.MapGroup("/dav").WithTags("WebDAV");
        dav.MapMethods("/{token}/{name}", ["OPTIONS"], Options);
        dav.MapMethods("/{token}/{name}", ["PROPFIND"], Propfind);
        dav.MapMethods("/{token}/{name}", ["HEAD", "GET"], Get);
        dav.MapMethods("/{token}/{name}", ["LOCK"], Lock);
        dav.MapMethods("/{token}/{name}", ["UNLOCK"], Unlock);
        dav.MapPut("/{token}/{name}", Put);
    }

    private static async Task<IResult> Mint(Guid vid, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs, IConfiguration cfg)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, version.DocumentId, ctx.RequestAborted);
        if (result != AccessResult.Ok)
            return result == AccessResult.NotFound
                ? Problem.Of(404, "Not found", "Version not found.")
                : Problem.Of(403, "Forbidden", "You do not have access to this document.");
        if (!DocumentAuthorization.CanEdit(role!.Value))
            return Problem.Of(403, "Forbidden", "Editor role required.");

        var session = new EditSession
        {
            Id = Guid.NewGuid(),
            DocumentId = version.DocumentId,
            BaseVersionId = vid,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(session);
        db.Add(Audit.Event(orgId, version.DocumentId, userId, "edit_session.opened",
            "session", session.Id.ToString(), new { baseVersionId = vid, mode = "webdav" }));
        await db.SaveChangesAsync(ctx.RequestAborted);

        var doc = await db.Documents.FirstAsync(d => d.Id == version.DocumentId, ctx.RequestAborted);
        var (_, ext) = await BlobMime.SniffAsync(blobs, version.BlobSha256, ctx.RequestAborted);
        var name = $"{BlobMime.StripKnownExtension(doc.Name)}.{ext}";
        var baseUrl = (cfg["PUBLIC_BASE_URL"] ?? "").TrimEnd('/');
        if (baseUrl.Length == 0) return Problem.Of(500, "Misconfigured", "PUBLIC_BASE_URL is not set.");
        var token = tokens.Issue(session.Id, userId, "w");
        var url = $"{baseUrl}/dav/{token}/{Uri.EscapeDataString(name)}";

        return Results.Created($"/api/v1/sessions/{session.Id}", new
        {
            sessionId = session.Id,
            url,
            // ofe|u| = "open for edit, from URL" — the registered protocol desktop Word handles.
            msWordUrl = $"ms-word:ofe|u|{url}",
            accessTokenTtlSeconds = WopiAccessToken.TtlSeconds,
        });
    }

    private sealed record DavAuth(EditSession? Session, IResult? Error);

    private static async Task<DavAuth> AuthorizeAsync(string token, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var claims = tokens.Validate(token);
        if (claims is null)
            return new(null, Results.Unauthorized());
        var session = await db.EditSessions.FirstOrDefaultAsync(
            s => s.Id == claims.Value.Sid && s.ClosedAt == null, ctx.RequestAborted);
        return session is null ? new(null, Results.Unauthorized()) : new(session, null);
    }

    private static async Task<IResult> Options(string token, string name, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        SetDavHeaders(ctx);
        return Results.Ok();
    }

    private static void SetDavHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["DAV"] = "1,2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV"; // what makes Office speak WebDAV, not FPRPC
        ctx.Response.Headers["Allow"] = "OPTIONS, GET, HEAD, PUT, PROPFIND, LOCK, UNLOCK";
    }

    private static async Task<IResult> Propfind(string token, string name, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        var sha = await CurrentShaAsync(db, session, ctx.RequestAborted);
        var blob = await db.Blobs.FirstAsync(b => b.Sha256 == sha, ctx.RequestAborted);
        var (mime, _) = await BlobMime.SniffAsync(blobs, sha, ctx.RequestAborted);

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>{ctx.Request.Path.Value}</D:href>
                <D:propstat>
                  <D:prop>
                    <D:displayname>{System.Security.SecurityElement.Escape(name)}</D:displayname>
                    <D:getcontentlength>{blob.SizeBytes}</D:getcontentlength>
                    <D:getlastmodified>{blob.CreatedAt.UtcDateTime:R}</D:getlastmodified>
                    <D:getcontenttype>{mime}</D:getcontenttype>
                    <D:resourcetype/>
                    <D:supportedlock>
                      <D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry>
                    </D:supportedlock>
                  </D:prop>
                  <D:status>HTTP/1.1 200 OK</D:status>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;
        return Results.Content(xml, "application/xml; charset=utf-8", statusCode: 207);
    }

    private static async Task<IResult> Get(string token, string name, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;

        var sha = await CurrentShaAsync(db, auth.Session!, ctx.RequestAborted);
        var (mime, _) = await BlobMime.SniffAsync(blobs, sha, ctx.RequestAborted);
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            var blob = await db.Blobs.FirstAsync(b => b.Sha256 == sha, ctx.RequestAborted);
            ctx.Response.Headers.ContentLength = blob.SizeBytes;
            ctx.Response.ContentType = mime;
            return Results.Ok();
        }
        return Results.Stream(await blobs.OpenReadAsync(sha, ctx.RequestAborted), mime);
    }

    private static async Task<IResult> Lock(string token, string name, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        // One exclusive lock per session, refresh allowed. Sessions are single-user, so contention
        // is with the browser editor sharing the row — same field WOPI locks use, on purpose.
        var live = session.LockValue is not null && session.LockExpiresAt > DateTimeOffset.UtcNow;
        var lockToken = live ? session.LockValue! : $"opaquelocktoken:{Guid.NewGuid()}";
        session.LockValue = lockToken;
        session.LockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(ctx.RequestAborted);

        ctx.Response.Headers["Lock-Token"] = $"<{lockToken}>";
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:prop xmlns:D="DAV:">
              <D:lockdiscovery>
                <D:activelock>
                  <D:locktype><D:write/></D:locktype>
                  <D:lockscope><D:exclusive/></D:lockscope>
                  <D:depth>0</D:depth>
                  <D:timeout>Second-1800</D:timeout>
                  <D:locktoken><D:href>{lockToken}</D:href></D:locktoken>
                </D:activelock>
              </D:lockdiscovery>
            </D:prop>
            """;
        return Results.Content(xml, "application/xml; charset=utf-8", statusCode: 200);
    }

    private static async Task<IResult> Unlock(string token, string name, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        var presented = ctx.Request.Headers["Lock-Token"].ToString().Trim('<', '>', ' ');
        if (session.LockValue is not null && presented == session.LockValue)
        {
            session.LockValue = null;
            session.LockExpiresAt = null;
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> Put(string token, string name, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs, VersioningService versioning)
    {
        var auth = await AuthorizeAsync(token, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        var put = await blobs.PutAsync(ctx.Request.Body, ctx.RequestAborted);
        var result = await versioning.CommitSaveAsync(
            new CommitInput(session.DocumentId, put.Sha256, put.SizeBytes, VersionSource.EditWebdav, session.UserId,
                SessionId: session.Id, BaseVersionId: session.BaseVersionId),
            ctx.RequestAborted);

        ctx.Response.Headers["X-EasyDocs-Version"] = result.VersionId.ToString();
        return Results.StatusCode(StatusCodes.Status204NoContent);
    }

    // Word re-GETs during an editing session; after its own save the freshest bytes are the ones it
    // wrote, not the minted base — LastCommittedSha tracks exactly that.
    private static async Task<string> CurrentShaAsync(EasyDocsDbContext db, EditSession session, CancellationToken ct)
        => session.LastCommittedSha
           ?? await db.Versions.Where(v => v.Id == session.BaseVersionId).Select(v => v.BlobSha256).FirstAsync(ct);
}
