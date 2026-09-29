using System.Text.Json.Serialization;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Editing;

// The WOPI host Collabora calls (spec §6). Auth is the WOPI access token carried as the `access_token`
// query param (Collabora holds no cookie) — these routes are NOT .RequireAuthorization(). Locks live on
// the EditSession row (30-min TTL), DB-backed, no Redis (spec §3).
public static class WopiEndpoints
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(30);

    public static void MapWopiEndpoints(this WebApplication app)
    {
        // fileId == sessionId. Mapped before the M0 /wopi/{**rest} 404 catch-all so these win on precedence.
        var g = app.MapGroup("").WithTags("WOPI");
        g.MapGet("/wopi/files/{fileId:guid}", CheckFileInfo);
        g.MapGet("/wopi/files/{fileId:guid}/contents", GetFile);
        g.MapPost("/wopi/files/{fileId:guid}/contents", PutFile);
        g.MapPost("/wopi/files/{fileId:guid}", LockOp);
    }

    private static async Task<IResult> CheckFileInfo(Guid fileId, HttpContext ctx, EasyDocsDbContext db,
        WopiAccessToken tokens, IBlobStore blobs, ILoggerFactory logs)
    {
        var auth = await Authorize(fileId, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        var doc = await db.Documents.FirstAsync(d => d.Id == session.DocumentId, ctx.RequestAborted);
        var baseVersion = await db.Versions.FirstAsync(v => v.Id == session.BaseVersionId, ctx.RequestAborted);
        var blob = await db.Blobs.FirstAsync(bl => bl.Sha256 == baseVersion.BlobSha256, ctx.RequestAborted);

        // Exactly one extension, and the blob's REAL one — same rule and same sniff as the R8 download
        // name (spec §5.3). Collabora shows this to the user and picks its editor from the extension,
        // so "… laundry lease.docx.docx" was both wrong and visible.
        string ext;
        try
        {
            (_, ext) = await BlobMime.SniffAsync(blobs, baseVersion.BlobSha256, ctx.RequestAborted);
        }
        catch (FileNotFoundException e)
        {
            return MissingBlob(logs, "CheckFileInfo", baseVersion.BlobSha256, e);
        }

        return Results.Ok(new CheckFileInfoResponse(
            $"{BlobMime.StripKnownExtension(doc.Name)}.{ext}",
            blob.SizeBytes,
            doc.CreatedBy.ToString(),
            auth.Uid.ToString(),
            "EasyDocs user",
            auth.Perms == "w",
            baseVersion.Id.ToString()));
    }

    /// <summary>
    /// The CheckFileInfo body (WOPI spec §6). Every name carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/> because these are PROTOCOL CONSTANTS, not house style:
    /// Program.cs installs ASP.NET's default camelCase PropertyNamingPolicy app-wide, which silently
    /// rewrote the anonymous object this used to be into `baseFileName` — and Collabora answers a
    /// non-conforming CheckFileInfo with "Unauthorized WOPI host", i.e. no document opens at all.
    /// The attribute beats any PropertyNamingPolicy, so this stays correct however the app's JSON
    /// options are configured later; scoped serializer options would only hold until the next
    /// `Results.Ok`. WopiHostTests asserts the raw wire bytes, not a (case-insensitive) DTO.
    /// </summary>
    private sealed record CheckFileInfoResponse(
        [property: JsonPropertyName("BaseFileName")] string BaseFileName,
        [property: JsonPropertyName("Size")] long Size,
        [property: JsonPropertyName("OwnerId")] string OwnerId,
        [property: JsonPropertyName("UserId")] string UserId,
        [property: JsonPropertyName("UserFriendlyName")] string UserFriendlyName,
        [property: JsonPropertyName("UserCanWrite")] bool UserCanWrite,
        [property: JsonPropertyName("Version")] string Version)
    {
        // Constant capability flags — what this host implements (LockOp below), not per-file state.
        [JsonPropertyName("SupportsLocks")] public bool SupportsLocks => true;
        [JsonPropertyName("SupportsUpdate")] public bool SupportsUpdate => true;
        [JsonPropertyName("SupportsGetLock")] public bool SupportsGetLock => true;
    }

    private static async Task<IResult> GetFile(Guid fileId, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens,
        IBlobStore blobs, ILoggerFactory logs)
    {
        var auth = await Authorize(fileId, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;

        var baseVersion = await db.Versions.FirstAsync(v => v.Id == auth.Session!.BaseVersionId, ctx.RequestAborted);
        try
        {
            var stream = await blobs.OpenReadAsync(baseVersion.BlobSha256, ctx.RequestAborted);
            return Results.Stream(stream, "application/octet-stream");
        }
        catch (FileNotFoundException e)
        {
            return MissingBlob(logs, "GetFile", baseVersion.BlobSha256, e);
        }
    }

    // A version row whose bytes the blob store can't produce is an OPERATOR problem (deleted blob,
    // mis-migrated store, wrong bucket/keys), not a client one — but if it escapes as an unhandled 500,
    // Collabora shows the user "Unauthorized WOPI host", which sends whoever debugs it in exactly the
    // wrong direction. Answer 404 and log the sha, so the server log names the real fault next to the
    // misleading dialog text people will be searching for.
    private static IResult MissingBlob(ILoggerFactory logs, string op, string sha, FileNotFoundException e)
    {
        logs.CreateLogger("WOPI").LogError(e,
            "WOPI {Op}: blob {Sha} is missing from the blob store — the version row exists but its bytes " +
            "don't. Collabora reports this to the user as 'Unauthorized WOPI host'. Check the blob store " +
            "(BlobStore/S3 settings; S3 keys are the bare sha256).", op, sha);
        return Problem.Of(404, "Blob missing",
            "The document's bytes are missing from the blob store. See the server log for the sha256.");
    }

    private static async Task<IResult> PutFile(Guid fileId, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens,
        IBlobStore blobs, VersioningService versioning)
    {
        var auth = await Authorize(fileId, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        // Enforce the lock: a live lock with a mismatched X-WOPI-Lock is a 409 (Collabora depends on it).
        if (IsLocked(session) && ctx.Request.Headers["X-WOPI-Lock"].ToString() != session.LockValue)
            return LockConflict(ctx, session.LockValue);

        var put = await blobs.PutAsync(ctx.Request.Body, ctx.RequestAborted);
        var result = await versioning.CommitSaveAsync(
            new CommitInput(session.DocumentId, put.Sha256, put.SizeBytes, VersionSource.EditWopi, session.UserId,
                SessionId: session.Id, BaseVersionId: session.BaseVersionId),
            ctx.RequestAborted);

        ctx.Response.Headers["X-WOPI-ItemVersion"] = result.VersionId.ToString();
        return Results.Ok(); // 200 also for a deduped (unchanged) re-PUT.
    }

    private static async Task<IResult> LockOp(Guid fileId, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var auth = await Authorize(fileId, ctx, db, tokens);
        if (auth.Error is not null) return auth.Error;
        var session = auth.Session!;

        var op = ctx.Request.Headers["X-WOPI-Override"].ToString();
        var requested = ctx.Request.Headers["X-WOPI-Lock"].ToString();
        var locked = IsLocked(session);

        switch (op)
        {
            case "GET_LOCK":
                ctx.Response.Headers["X-WOPI-Lock"] = locked ? session.LockValue : "";
                return Results.Ok();

            case "LOCK":
                if (!locked || session.LockValue == requested)
                {
                    session.LockValue = requested;
                    session.LockExpiresAt = DateTimeOffset.UtcNow + LockTtl;
                    await db.SaveChangesAsync(ctx.RequestAborted);
                    return Results.Ok();
                }
                return LockConflict(ctx, session.LockValue);

            case "REFRESH_LOCK":
                if (locked && session.LockValue == requested)
                {
                    session.LockExpiresAt = DateTimeOffset.UtcNow + LockTtl;
                    await db.SaveChangesAsync(ctx.RequestAborted);
                    return Results.Ok();
                }
                return LockConflict(ctx, locked ? session.LockValue : "");

            case "UNLOCK":
                if (locked && session.LockValue == requested)
                {
                    session.LockValue = null;
                    session.LockExpiresAt = null;
                    await db.SaveChangesAsync(ctx.RequestAborted);
                    return Results.Ok();
                }
                return LockConflict(ctx, locked ? session.LockValue : "");

            default:
                return Results.BadRequest();
        }
    }

    private static bool IsLocked(EditSession s) =>
        s.LockValue is not null && s.LockExpiresAt > DateTimeOffset.UtcNow;

    private static IResult LockConflict(HttpContext ctx, string? storedLock)
    {
        ctx.Response.Headers["X-WOPI-Lock"] = storedLock ?? "";
        return Results.StatusCode(StatusCodes.Status409Conflict);
    }

    // Validate the query-param token, confirm its Sid matches the route fileId, then load the open session.
    private static async Task<AuthResult> Authorize(Guid fileId, HttpContext ctx, EasyDocsDbContext db, WopiAccessToken tokens)
    {
        var token = ctx.Request.Query["access_token"].ToString();
        var parsed = string.IsNullOrEmpty(token) ? null : tokens.Validate(token);
        if (parsed is null || parsed.Value.Sid != fileId)
            return new AuthResult(Results.Unauthorized(), null, Guid.Empty, "");

        var session = await db.EditSessions.FirstOrDefaultAsync(
            s => s.Id == fileId && s.ClosedAt == null, ctx.RequestAborted);
        return session is null
            ? new AuthResult(Results.NotFound(), null, Guid.Empty, "")
            : new AuthResult(null, session, parsed.Value.Uid, parsed.Value.Perms);
    }

    private readonly record struct AuthResult(IResult? Error, EditSession? Session, Guid Uid, string Perms);
}
