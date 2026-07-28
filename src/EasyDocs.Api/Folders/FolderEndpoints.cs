using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Folders;

public static class FolderEndpoints
{
    public record CreateRequest(string? Name, Guid? ParentId);
    public record UpdateRequest(string? Name, Guid? ParentId);

    public static void MapFolderEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/v1/folders").RequireAuthorization().WithTags("Folders");
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPatch("/{id:guid}", Update);
        g.MapDelete("/{id:guid}", Delete);
    }

    private static async Task<IResult> List(HttpContext ctx, EasyDocsDbContext db, Guid? parentId)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var items = await db.Folders
            .Where(f => f.OrgId == orgId && f.DeletedAt == null && f.ParentId == parentId)
            .OrderBy(f => f.Name)
            .Select(f => new { f.Id, f.Name, f.ParentId })
            .ToListAsync();
        return Results.Ok(items);
    }

    private static async Task<IResult> Create(CreateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0)
            return Problem.Of(400, "Invalid request", "name is required.");
        if (req.ParentId is { } pid && !await ExistsAsync(db, orgId, pid))
            return Problem.Of(400, "Invalid parent", "parentId does not exist in your org.");

        // Pre-check: the DB unique index treats NULL ParentId as distinct, so it can't guard
        // root-level duplicates. The DbUpdateException catch below covers the concurrent-nested case.
        if (await db.Folders.AnyAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.ParentId == req.ParentId && f.Name == name))
            return Problem.Of(409, "Duplicate name", "A folder with that name already exists here.");

        var folder = new Folder { Id = Guid.NewGuid(), OrgId = orgId, ParentId = req.ParentId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(folder);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Problem.Of(409, "Duplicate name", "A folder with that name already exists here."); }
        return Results.Created($"/api/v1/folders/{folder.Id}", new { folder.Id, folder.Name, folder.ParentId });
    }

    private static async Task<IResult> Update(Guid id, UpdateRequest req, HttpContext ctx, EasyDocsDbContext db)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);
        if (folder is null) return Problem.Of(404, "Not found", "Folder not found.");

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0) return Problem.Of(400, "Invalid request", "name cannot be empty.");
            folder.Name = name;
        }
        if (req.ParentId is { } pid)
        {
            if (pid == id) return Problem.Of(400, "Invalid parent", "A folder cannot be its own parent.");
            if (!await ExistsAsync(db, orgId, pid)) return Problem.Of(400, "Invalid parent", "parentId does not exist in your org.");
            folder.ParentId = pid;
        }

        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException) { return Problem.Of(409, "Duplicate name", "A folder with that name already exists here."); }
        return Results.Ok(new { folder.Id, folder.Name, folder.ParentId });
    }

    private static async Task<IResult> Delete(Guid id, HttpContext ctx, EasyDocsDbContext db, string? mode)
    {
        var orgId = CurrentUser.OrgId(ctx.User);
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);
        if (folder is null) return Problem.Of(404, "Not found", "Folder not found.");

        var children = await db.Folders
            .Where(f => f.OrgId == orgId && f.DeletedAt == null && f.ParentId == id)
            .ToListAsync();

        if (children.Count > 0 && mode is null)
            return Problem.Of(400, "Mode required", "Folder is not empty; choose mode=trash or mode=promote_children.");

        var now = DateTimeOffset.UtcNow;
        if (mode == "promote_children")
            foreach (var child in children) child.ParentId = folder.ParentId;
        // mode=trash (or empty folder): soft-delete just this folder; children left in place.

        folder.DeletedAt = now;
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static Task<bool> ExistsAsync(EasyDocsDbContext db, Guid orgId, Guid id) =>
        db.Folders.AnyAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);
}
