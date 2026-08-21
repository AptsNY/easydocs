using EasyDocs.Api.Api;
using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Versioning;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

public static class DocumentEndpoints
{
    public record CreateDocumentRequest(string? Name, Guid? FolderId);
    public record UpdateRequest(string? Name, Guid? FolderId);
    public record VersionCounterRequest(int Major, int Minor, int Rev);

    public static void MapDocumentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/documents").RequireAuthorization().WithTags("Documents");
        g.MapGet("", ListDocuments);
        g.MapPost("", Create);
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update);
        g.MapGet("/{id:guid}/versions", ListVersions);
        g.MapPost("/{id:guid}/versions", Upload).DisableAntiforgery();
        g.MapPost("/{id:guid}/versions:import", Import).DisableAntiforgery();
        g.MapGet("/{id:guid}/compare", Compare);
        g.MapPut("/{id:guid}/version-counter", SetVersionCounter);
        g.MapDelete("/{id:guid}", Trash);
        g.MapPost("/{id:guid}:restore", Restore);

        var v = app.MapGroup("/api/v1/versions").RequireAuthorization().WithTags("Documents");
        v.MapGet("/{vid:guid}", GetVersion);
        v.MapGet("/{vid:guid}/download", Download);
    }

    // Version detail (spec §10.1). Viewer+ suffices, same chokepoint as Download.
    private static async Task<IResult> GetVersion(Guid vid, HttpContext ctx, EasyDocsDbContext db)
    {
        var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == vid, ctx.RequestAborted);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (_, failure) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false);
        if (failure is not null) return failure;

        return Results.Ok(new
        {
            id = version.Id,
            documentId = version.DocumentId,
            major = version.Major,
            minor = version.Minor,
            revision = version.Revision,
            name = version.Name,
            source = version.Source.ToString(),
            publishedKind = version.PublishedKind,
            publishedAt = version.PublishedAt,
            publishName = version.PublishName,
            hasPdf = version.PdfBlobSha256 is not null,
            parentVersionId = version.ParentVersionId,
            createdAt = version.CreatedAt,
            createdBy = version.CreatedBy,
        });
    }

    // R8 download (spec §5.3): name the file "{orgSlug}__{Sanitized_Name}-v{M}.{m}.{r}.{ext}".
    // Viewer+ suffices; pdf requires a published PDF blob (else 409).
    private static async Task<IResult> Download(Guid vid, string? format, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs)
    {
        var version = await db.Versions.FirstOrDefaultAsync(x => x.Id == vid);
        if (version is null) return Problem.Of(404, "Not found", "Version not found.");

        var (doc, failure) = await AuthorizeAsync(db, ctx, version.DocumentId, requireEdit: false);
        if (failure is not null) return failure;

        var slug = await db.Organizations.Where(o => o.Id == doc!.OrgId).Select(o => o.Slug).FirstAsync();
        var counter = (version.Major, version.Minor, version.Revision);

        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (version.PdfBlobSha256 is null)
                return Problem.Of(409, "No PDF", "This version has no PDF (publish it first).");
            var name = Numbering.DownloadFileName(slug, doc!.Name, counter, "pdf");
            return Results.Stream(await blobs.OpenReadAsync(version.PdfBlobSha256, ctx.RequestAborted), "application/pdf", name);
        }

        // The stored bytes, labelled as what they ARE (spec §5.3 / R8). Most are .docx; the corpus also
        // holds PDFs and legacy .doc files, and serving those as docx made Word refuse a file whose bytes
        // were perfectly good. Sniffed rather than read from Blobs.Mime so rows written before this fix
        // (all hardcoded docx) serve correctly too.
        var (mime, ext) = await BlobMime.SniffAsync(blobs, version.BlobSha256, ctx.RequestAborted);
        var fileName = Numbering.DownloadFileName(slug, doc!.Name, counter, ext);
        return Results.Stream(await blobs.OpenReadAsync(version.BlobSha256, ctx.RequestAborted), mime, fileName);
    }

    // R5 manual override: set the authoritative counter under the same per-document FOR UPDATE lock as
    // the write path (spec §5.1), so a subsequent CommitSaveAsync NextDraft continues from it (R6).
    private static async Task<IResult> SetVersionCounter(Guid id, VersionCounterRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: true);
        if (failure is not null) return failure;

        try { Numbering.Manual(req.Major, req.Minor, req.Rev); }
        catch (ArgumentOutOfRangeException) { return Problem.Of(400, "Invalid request", "Counter values must be non-negative."); }

        await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {id} FOR UPDATE", ctx.RequestAborted);
        doc!.VersionCounterMajor = req.Major;
        doc.VersionCounterMinor = req.Minor;
        doc.VersionCounterRev = req.Rev;
        db.Add(Audit.Event(doc.OrgId, id, CurrentUser.UserId(ctx.User), "version_counter.set",
            "document", id.ToString(), new { number = $"{req.Major}.{req.Minor}.{req.Rev}" }));
        await db.SaveChangesAsync(ctx.RequestAborted);
        await tx.CommitAsync(ctx.RequestAborted);

        return Results.Ok(new { major = req.Major, minor = req.Minor, rev = req.Rev });
    }

    // Which column a cursor's key came from. `created` reuses Pagination.CreatedTag so a creation-time
    // cursor means the same thing here as it does on every other paginated endpoint.
    private const byte SortCreated = Pagination.CreatedTag;
    private const byte SortUpdated = 1;
    private const byte SortName = 2;

    private static byte? SortTag(string? sort) => (sort ?? "").ToLowerInvariant() switch
    {
        "" or "created" => SortCreated,
        "updated" => SortUpdated,
        "name" => SortName,
        _ => null,
    };

    // Dashboard list (spec §9/§10): documents the caller is a member of, org-scoped, optional
    // folderId/q filters, cursor-paginated. `trashed=true` swaps the DeletedAt filter so the SPA's
    // trash view can reach :restore — membership scoping is identical either way (spec §11).
    private static async Task<IResult> ListDocuments(
        HttpContext ctx, EasyDocsDbContext db, Guid? folderId, string? q, string? cursor, int? limit,
        bool? trashed, string? sort, string? order)
    {
        if (SortTag(sort) is not { } tag)
            return Problem.Of(400, "Invalid sort", "sort must be one of: created, updated, name.");

        var after = Pagination.Decode(cursor);
        if (after is not null && after.Tag != tag)
            return Problem.Of(400, "Cursor mismatch",
                "This cursor was issued for a different sort order. Drop the cursor when you change sort.");

        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);

        var membership = db.Documents.Where(d => d.OrgId == orgId
            && db.DocumentMembers.Any(m => m.DocumentId == d.Id && m.UserId == userId));
        var query = trashed is true
            ? membership.Where(d => d.DeletedAt != null)
            : membership.Where(d => d.DeletedAt == null);
        if (folderId is { } fid) query = query.Where(d => d.FolderId == fid);
        // Name OR content (issue #12): content matches the tsvector index built from each document's
        // main head. websearch syntax ("lease agreement", "cat -dog") with the same language-neutral
        // 'simple' config the index uses — mixing configs would make the GIN index unusable.
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(d => EF.Functions.ILike(d.Name, $"%{q}%")
                || db.DocumentTexts.Any(t => t.DocumentId == d.Id
                    && t.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", q))));

        var page = await Pagination.PageAsync(query, cursor, limit, descending: false, ctx.RequestAborted);
        return Results.Ok(new
        {
            items = await DocumentListProjection.BuildAsync(db, page.Items, ctx.RequestAborted),
            nextCursor = page.NextCursor,
        });
    }

    private static async Task<IResult> Create(CreateDocumentRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Problem.Of(400, "Invalid request", "name is required.");
        if (req.FolderId is { } fid && !await FolderExistsAsync(db, orgId, fid))
            return Problem.Of(400, "Invalid folder", "folderId does not exist in your org.");

        var now = DateTimeOffset.UtcNow;
        var doc = new Document
        {
            Id = Guid.NewGuid(), OrgId = orgId, FolderId = req.FolderId, Name = name,
            VersionCounterMajor = 0, VersionCounterMinor = 0, VersionCounterRev = 0,
            CreatedBy = userId, CreatedAt = now,
        };
        db.Add(doc);
        db.Add(new Branch { Id = Guid.NewGuid(), DocumentId = doc.Id, Ordinal = 0, Kind = BranchKind.Main, CreatedAt = now });
        db.Add(new DocumentMember { DocumentId = doc.Id, UserId = userId, Role = DocRole.Owner, CreatedAt = now });
        db.Add(Audit.Event(orgId, doc.Id, userId, "document.created", "document", doc.Id.ToString(),
            new { name = doc.Name, folderId = doc.FolderId }));
        await db.SaveChangesAsync(); // single SaveChanges = one transaction

        return Results.Created($"/api/v1/documents/{doc.Id}", new { id = doc.Id, name = doc.Name, folderId = doc.FolderId });
    }

    private static async Task<IResult> Get(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: false);
        if (failure is not null) return failure;
        return Results.Ok(new { id = doc!.Id, name = doc.Name, folderId = doc.FolderId, orgId = doc.OrgId });
    }

    private static async Task<IResult> Update(Guid id, UpdateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var (doc, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: true);
        if (failure is not null) return failure;

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0) return Problem.Of(400, "Invalid request", "name cannot be empty.");
            doc!.Name = name;
        }
        if (req.FolderId is { } fid)
        {
            if (!await FolderExistsAsync(db, orgId, fid))
                return Problem.Of(400, "Invalid folder", "folderId does not exist in your org.");
            doc!.FolderId = fid;
        }
        db.Add(Audit.Event(orgId, doc!.Id, CurrentUser.UserId(ctx.User), "document.updated", "document", id.ToString(),
            new { name = doc.Name, folderId = doc.FolderId }));
        await db.SaveChangesAsync();
        return Results.Ok(new { id = doc!.Id, name = doc.Name, folderId = doc.FolderId });
    }

    // Trash (spec §10.1). Soft-delete only: versions, blobs and members stay put so :restore is lossless.
    // Owner-only. A trashed document drops out of every other route via the DeletedAt == null filter in
    // DocumentAuthorization, so a second DELETE is a 404.
    private static async Task<IResult> Trash(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, _, failure) = await DocumentAuthorization.AuthorizeAsync(db, ctx, id, Need.Own, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        doc!.DeletedAt = DateTimeOffset.UtcNow;
        db.Add(Audit.Event(doc.OrgId, doc.Id, CurrentUser.UserId(ctx.User), "document.trashed", "document", id.ToString(), null));
        await db.SaveChangesAsync(ctx.RequestAborted);
        return Results.NoContent();
    }

    // Restore from trash. Resolves with includeDeleted since the target is by definition trashed;
    // restoring a live document is a no-op (its postcondition already holds).
    private static async Task<IResult> Restore(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var (doc, _, failure) = await DocumentAuthorization.AuthorizeAsync(
            db, ctx, id, Need.Own, includeDeleted: true, ct: ctx.RequestAborted);
        if (failure is not null) return failure;

        if (doc!.DeletedAt is not null)
        {
            doc.DeletedAt = null;
            db.Add(Audit.Event(doc.OrgId, doc.Id, CurrentUser.UserId(ctx.User), "document.restored", "document", id.ToString(), null));
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        return Results.Ok(new { id = doc.Id, name = doc.Name, folderId = doc.FolderId });
    }

    // Console history (spec §9). `order=desc` is opt-in: the ascending default is load-bearing for the
    // E-suite's oldest-first assertions, so the UI asks for desc rather than the default flipping.
    private static async Task<IResult> ListVersions(
        Guid id, HttpContext ctx, EasyDocsDbContext db, string? cursor, int? limit, string? order)
    {
        var (_, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: false);
        if (failure is not null) return failure;

        var descending = Pagination.Descending(order);
        var page = await Pagination.PageAsync(
            db.Versions.Where(v => v.DocumentId == id), cursor, limit, descending, ctx.RequestAborted);

        return Results.Ok(new
        {
            items = await VersionListProjection.BuildAsync(db, page.Items, ctx.RequestAborted),
            nextCursor = page.NextCursor,
        });
    }

    private static Task<IResult> Upload(Guid id, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs, VersioningService versioning) =>
        SaveAsync(id, ctx, db, blobs, versioning, VersionSource.Upload);

    private static Task<IResult> Import(Guid id, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs, VersioningService versioning) =>
        SaveAsync(id, ctx, db, blobs, versioning, VersionSource.Import);

    // The single HTTP write path: store the blob, then route through VersioningService.CommitSaveAsync
    // (spec §5.2). Upload and import differ only by VersionSource.
    private static async Task<IResult> SaveAsync(Guid id, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs, VersioningService versioning, VersionSource source)
    {
        var userId = CurrentUser.UserId(ctx.User);
        var (_, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: true);
        if (failure is not null) return failure;

        // Both ingest routes (§10.3) funnel through here, so this is the one place that has to survive a
        // hostile body. Reading Request.Form throws on a non-multipart or malformed multipart request
        // (e.g. a bad Content-Disposition), which would surface as a 500 on a public endpoint; a bad
        // request must be an RFC-7807 400.
        if (!ctx.Request.HasFormContentType)
            return Problem.Of(400, "Invalid request", "Expected a multipart/form-data body with a file field.");

        IFormFile? file;
        try
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            file = form.Files["file"] ?? form.Files.FirstOrDefault();
        }
        catch (InvalidDataException)
        {
            return Problem.Of(400, "Invalid request", "The multipart body could not be parsed.");
        }
        if (file is null || file.Length == 0) return Problem.Of(400, "Invalid request", "A non-empty file field is required.");

        BlobResult stored;
        await using (var upload = file.OpenReadStream())
            stored = await blobs.PutAsync(upload, ctx.RequestAborted);

        // Blobs.Mime must record what was actually stored, not "docx" by assumption (spec §5.2). Sniffed
        // from the stored bytes: file.ContentType and file.FileName are both attacker-controlled.
        var (mime, _) = await BlobMime.SniffAsync(blobs, stored.Sha256, ctx.RequestAborted);

        var result = await versioning.CommitSaveAsync(
            new CommitInput(id, stored.Sha256, stored.SizeBytes, source, userId, Mime: mime), ctx.RequestAborted);

        return Results.Created($"/api/v1/documents/{id}/versions/{result.VersionId}",
            new { versionId = result.VersionId, major = result.Major, minor = result.Minor, revision = result.Revision });
    }

    // Compare two versions (spec §7). Viewer+ suffices. summary = numeric counts (eager cache, else
    // computed inline; 422 if the pair cannot be compared); html = on-demand cached redline (200 text/html,
    // graceful message if unavailable — it is rendered, not parsed); docx = the compared redline docx blob,
    // 422 if unavailable. Every WmlComparer call is guarded inside the diff service.
    private static async Task<IResult> Compare(
        Guid id, Guid from, Guid to, string? format,
        HttpContext ctx, EasyDocsDbContext db, WmlComparerDiffService diff, IBlobStore blobs)
    {
        var (_, failure) = await AuthorizeAsync(db, ctx, id, requireEdit: false);
        if (failure is not null) return failure;

        var fromSha = await db.Versions.Where(v => v.Id == from && v.DocumentId == id).Select(v => v.BlobSha256).FirstOrDefaultAsync();
        var toSha = await db.Versions.Where(v => v.Id == to && v.DocumentId == id).Select(v => v.BlobSha256).FirstOrDefaultAsync();
        if (fromSha is null || toSha is null)
            return Problem.Of(404, "Not found", "from/to must reference versions of this document.");

        switch ((format ?? "summary").ToLowerInvariant())
        {
            case "html":
            {
                var render = await diff.RedlineHtmlAsync(fromSha, toSha, ctx.RequestAborted);
                return Results.Content(render.Available ? render.Html! : "<p>Comparison unavailable.</p>", "text/html");
            }
            case "docx":
            {
                await diff.RedlineHtmlAsync(fromSha, toSha, ctx.RequestAborted); // ensures the redline blob exists
                var redline = await db.VersionDiffs
                    .Where(x => x.FromSha256 == fromSha && x.ToSha256 == toSha)
                    .Select(x => x.RedlineBlobSha256).FirstOrDefaultAsync();
                if (redline is null)
                    return Problem.Of(422, "Comparison unavailable", "A redline document could not be produced.");
                return Results.Stream(await blobs.OpenReadAsync(redline, ctx.RequestAborted), BlobMime.Docx);
            }
            default:
            {
                // A cached row with a non-null Insertions is a comparison that SUCCEEDED. Null means the
                // eager worker has not drained the job (or it degraded), so compute inline — the same
                // "null = no computed diff" convention VersionListProjection.SummariesAsync reads.
                var cached = await db.VersionDiffs.FirstOrDefaultAsync(x => x.FromSha256 == fromSha && x.ToSha256 == toSha);
                if (cached?.Insertions is not null)
                    return Results.Ok(new
                    {
                        insertions = cached.Insertions.Value, deletions = cached.Deletions ?? 0,
                        moves = cached.Moves ?? 0, formatChanges = cached.FormatChanges ?? 0,
                    });

                var s = await diff.SummaryAsync(fromSha, toSha, ctx.RequestAborted);
                // spec §7: WmlComparer degrading to Available=false means "these two cannot be compared",
                // which is NOT "they are identical". Reporting its zeros made a failure indistinguishable
                // from a genuine 0/0 — and a genuine 0/0 is real and common (a re-save with no wording
                // change), so it must stay a 200. 422 is the answer ?format=docx already gives for exactly
                // this condition; the three formats now agree on WHETHER a comparison exists.
                if (!s.Available)
                    return Problem.Of(422, "Comparison unavailable", "These two versions could not be compared.");
                return Results.Ok(new
                {
                    insertions = s.Insertions, deletions = s.Deletions,
                    moves = s.Moves, formatChanges = s.FormatChanges,
                });
            }
        }
    }

    // Single authorization chokepoint (spec §10/§11). Resolves the caller's document role with no org-role
    // fallback, then maps failures to IResult: cross-org/missing -> 404, same-org non-member -> 403,
    // and (when requireEdit) Viewer -> 403. On success returns the loaded doc and a null failure.
    private static async Task<(Document? Doc, IResult? Failure)> AuthorizeAsync(
        EasyDocsDbContext db, HttpContext ctx, Guid id, bool requireEdit)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        var (result, role) = await DocumentAuthorization.ResolveAsync(db, orgId, userId, id);
        switch (result)
        {
            case AccessResult.NotFound:
                return (null, Problem.Of(404, "Not found", "Document not found."));
            case AccessResult.Forbidden:
                return (null, Problem.Of(403, "Forbidden", "You do not have access to this document."));
        }
        if (requireEdit && !DocumentAuthorization.CanEdit(role!.Value))
            return (null, Problem.Of(403, "Forbidden", "Editor role required."));

        var doc = await db.Documents.FirstAsync(d => d.Id == id);
        return (doc, null);
    }

    private static Task<bool> FolderExistsAsync(EasyDocsDbContext db, Guid orgId, Guid id) =>
        db.Folders.AnyAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);
}
