using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Documents;

public static class DocumentEndpoints
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public record CreateRequest(string? Name, Guid? FolderId);
    public record UpdateRequest(string? Name, Guid? FolderId);

    public static void MapDocumentEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/documents").RequireAuthorization();
        g.MapPost("", Create);
        g.MapGet("/{id:guid}", Get);
        g.MapPatch("/{id:guid}", Update);
        g.MapGet("/{id:guid}/versions", ListVersions);
        g.MapPost("/{id:guid}/versions", Upload).DisableAntiforgery();
    }

    private static async Task<IResult> Create(CreateRequest req, HttpContext ctx, EasyDocsDbContext db)
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
        await db.SaveChangesAsync(); // single SaveChanges = one transaction

        return Results.Created($"/api/v1/documents/{doc.Id}", new { id = doc.Id, name = doc.Name, folderId = doc.FolderId });
    }

    private static async Task<IResult> Get(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        var doc = await FindAsync(db, ctx, id);
        return doc is null
            ? Problem.Of(404, "Not found", "Document not found.")
            : Results.Ok(new { id = doc.Id, name = doc.Name, folderId = doc.FolderId, orgId = doc.OrgId });
    }

    private static async Task<IResult> Update(Guid id, UpdateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var doc = await FindAsync(db, ctx, id);
        if (doc is null) return Problem.Of(404, "Not found", "Document not found.");

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0) return Problem.Of(400, "Invalid request", "name cannot be empty.");
            doc.Name = name;
        }
        if (req.FolderId is { } fid)
        {
            if (!await FolderExistsAsync(db, orgId, fid))
                return Problem.Of(400, "Invalid folder", "folderId does not exist in your org.");
            doc.FolderId = fid;
        }
        await db.SaveChangesAsync();
        return Results.Ok(new { id = doc.Id, name = doc.Name, folderId = doc.FolderId });
    }

    private static async Task<IResult> ListVersions(Guid id, HttpContext ctx, EasyDocsDbContext db)
    {
        if (await FindAsync(db, ctx, id) is null) return Problem.Of(404, "Not found", "Document not found.");
        var items = await db.Versions
            .Where(v => v.DocumentId == id)
            .OrderBy(v => v.CreatedAt)
            .Select(v => new { id = v.Id, major = v.Major, minor = v.Minor, revision = v.Revision, source = v.Source.ToString(), createdAt = v.CreatedAt, createdBy = v.CreatedBy })
            .ToListAsync();
        return Results.Ok(items);
    }

    private static async Task<IResult> Upload(Guid id, HttpContext ctx, EasyDocsDbContext db, IBlobStore blobs)
    {
        var userId = CurrentUser.UserId(ctx.User);
        if (await FindAsync(db, ctx, id) is null) return Problem.Of(404, "Not found", "Document not found.");

        var file = ctx.Request.Form.Files["file"] ?? ctx.Request.Form.Files.FirstOrDefault();
        if (file is null || file.Length == 0) return Problem.Of(400, "Invalid request", "A non-empty file field is required.");

        BlobResult stored;
        await using (var upload = file.OpenReadStream())
            stored = await blobs.PutAsync(upload, ctx.RequestAborted);

        // Blobs are content-addressed and immutable — insert only if this sha is new.
        if (!await db.Blobs.AnyAsync(bl => bl.Sha256 == stored.Sha256))
        {
            db.Add(new Blob { Sha256 = stored.Sha256, SizeBytes = stored.SizeBytes, Mime = DocxMime, StorageKey = stored.Sha256, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // Per-document row lock so the authoritative counter increment (spec §5.1) is race-safe.
        await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {id} FOR UPDATE", ctx.RequestAborted);

        var doc = await db.Documents.FirstAsync(d => d.Id == id, ctx.RequestAborted);
        doc.VersionCounterRev += 1;

        var main = await db.Branches.FirstAsync(b => b.DocumentId == id && b.Ordinal == 0, ctx.RequestAborted);
        var maxSeq = await db.Versions.Where(v => v.BranchId == main.Id).Select(v => (int?)v.SeqInBranch).MaxAsync(ctx.RequestAborted) ?? 0;

        var version = new DocumentVersion
        {
            Id = Guid.NewGuid(), DocumentId = id, BranchId = main.Id, SeqInBranch = maxSeq + 1,
            Major = doc.VersionCounterMajor, Minor = doc.VersionCounterMinor, Revision = doc.VersionCounterRev,
            Source = VersionSource.Upload, BlobSha256 = stored.Sha256, CreatedBy = userId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Add(version);
        await db.SaveChangesAsync(ctx.RequestAborted);
        await tx.CommitAsync(ctx.RequestAborted);

        return Results.Created($"/api/v1/documents/{id}/versions/{version.Id}",
            new { versionId = version.Id, major = version.Major, minor = version.Minor, revision = version.Revision });
    }

    // TODO(Task 9): replace with DocumentAuthorization.ResolveRole. For M0, require the caller be a member of the doc in their org.
    private static Task<Document?> FindAsync(EasyDocsDbContext db, HttpContext ctx, Guid id)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var userId = CurrentUser.UserId(ctx.User);
        return db.Documents.FirstOrDefaultAsync(d =>
            d.Id == id && d.OrgId == orgId && d.DeletedAt == null &&
            db.DocumentMembers.Any(m => m.DocumentId == d.Id && m.UserId == userId));
    }

    private static Task<bool> FolderExistsAsync(EasyDocsDbContext db, Guid orgId, Guid id) =>
        db.Folders.AnyAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);
}
