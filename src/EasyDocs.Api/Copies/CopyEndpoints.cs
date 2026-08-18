using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Copies;

// Push To Copy — the fork half of copies & push (spec §8, §10.1, E8's 8th action, E9).
//
// A copy is nothing exotic: an ordinary documents row carrying ParentDocumentId + ForkedFromVersionId,
// whose first version references the SAME immutable blob as the forked version (zero-copy — blobs are
// content-addressed, so "copying" a document copies no bytes).
//
// The isolation that makes this the signature feature is a *consequence* of the existing chokepoint,
// not new code: the copy gets its own document_members starting with only its creator, and
// DocumentAuthorization has no parent-document fallback, so a reviewer invited to the copy resolves no
// role on the master and never sees its drafts (spec §11, E12).
public static class CopyEndpoints
{
    public record CreateCopyRequest(string? Name);

    public static void MapCopyEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("").RequireAuthorization().WithTags("Copies");
        g.MapPost("/api/v1/versions/{vid:guid}/copies", Fork);
        g.MapGet("/api/v1/documents/{id:guid}/copies", List);
    }

    // Fork a specific version into a new isolated document. Editor+ on the SOURCE: forking lifts content
    // out of a document, which is a content-level privilege a Viewer does not hold.
    private static async Task<IResult> Fork(
        Guid vid, CreateCopyRequest? req, HttpContext ctx, EasyDocsDbContext db, VersioningService versioning)
    {
        var source = await db.Versions.FirstOrDefaultAsync(v => v.Id == vid, ctx.RequestAborted);
        if (source is null) return Problem.Of(404, "Not found", "Version not found.");

        var (master, _, failure) = await DocumentAuthorization.AuthorizeAsync(
            db, ctx, source.DocumentId, Need.Edit, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var userId = CurrentUser.UserId(ctx.User);
        var name = req?.Name?.Trim() is { Length: > 0 } given ? given : $"{master!.Name} (copy)";
        var now = DateTimeOffset.UtcNow;

        var copy = new Document
        {
            Id = Guid.NewGuid(),
            OrgId = master!.OrgId,
            FolderId = master.FolderId,
            Name = name,
            ParentDocumentId = master.Id,
            ForkedFromVersionId = source.Id,
            // The copy's history starts from scratch: its own counter, so its first version is 0.0.1 and
            // the master's numbering is untouched (spec §5.1 — the counter is per document).
            VersionCounterMajor = 0, VersionCounterMinor = 0, VersionCounterRev = 0,
            CreatedBy = userId, CreatedAt = now,
        };
        db.Add(copy);
        db.Add(new Branch { Id = Guid.NewGuid(), DocumentId = copy.Id, Ordinal = 0, Kind = BranchKind.Main, CreatedAt = now });
        // ONLY the creator. Master members are deliberately not carried over (spec §11) — that is the
        // whole point of a copy, and it is enforced by simply not copying the roster.
        db.Add(new DocumentMember { DocumentId = copy.Id, UserId = userId, Role = DocRole.Owner, CreatedAt = now });
        db.Add(Audit.Event(copy.OrgId, copy.Id, userId, "document.created", "document", copy.Id.ToString(),
            new { name = copy.Name, forkedFromVersionId = source.Id }));
        // Also recorded against the MASTER: its owners must be able to see that content was forked out.
        db.Add(Audit.Event(copy.OrgId, master.Id, userId, "copy.created", "document", copy.Id.ToString(),
            new { name = copy.Name, fromVersionId = source.Id }));
        await db.SaveChangesAsync(ctx.RequestAborted); // one SaveChanges = one transaction

        // Zero-copy: commit the source blob as the copy's first version through the single write path
        // (spec §5.2), so numbering, the audit row and the SSE broadcast all behave as for any other write.
        var size = await db.Blobs.Where(b => b.Sha256 == source.BlobSha256).Select(b => b.SizeBytes)
            .FirstAsync(ctx.RequestAborted);
        var first = await versioning.CommitSaveAsync(
            new CommitInput(copy.Id, source.BlobSha256, size, VersionSource.CopyPush, userId), ctx.RequestAborted);

        return Results.Created($"/api/v1/documents/{copy.Id}", Dto(copy, first.VersionId));
    }

    // The copies of a document. Any member of the master may see that forks exist; the forks' *contents*
    // remain behind their own membership.
    private static async Task<IResult> List(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var (_, _, failure) = await DocumentAuthorization.AuthorizeAsync(
            db, ctx, id, Need.Read, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        var copies = await db.Documents
            .Where(d => d.ParentDocumentId == id && d.DeletedAt == null)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToListAsync(ctx.RequestAborted);

        return Results.Ok(copies.Select(c => Dto(c, null)));
    }

    private static object Dto(Document copy, Guid? versionId) => new
    {
        id = copy.Id,
        name = copy.Name,
        parentDocumentId = copy.ParentDocumentId,
        forkedFromVersionId = copy.ForkedFromVersionId,
        versionId,
        createdAt = copy.CreatedAt,
    };
}
