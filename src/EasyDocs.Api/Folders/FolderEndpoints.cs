using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Folders;

public static class FolderEndpoints
{
    public record CreateFolderRequest(string? Name, Guid? ParentId);

    /// <summary>
    /// PATCH body. Every field is optional, and for ParentId "absent" and "null" have to mean different
    /// things — leave the parent alone vs. move to the root. A positional `Guid?` collapses both to null,
    /// which is why a folder could be nested but never un-nested.
    ///
    /// A settable property with a "was it set" flag is how System.Text.Json expresses that natively: the
    /// setter runs for an explicit `"parentId": null` and does not run when the key is absent. Chosen
    /// over the alternatives because it costs three lines and changes nothing a caller can see —
    /// `parentId` stays a nullable GUID in the OpenAPI schema, and `null` means what JSON says it means.
    /// A magic string sentinel ("none") would have to widen the field to `string` and teach every client
    /// a word that is not a folder id; a `JsonElement` would erase the schema.
    ///
    /// ponytail: presence-tracking is inlined here rather than generalised into an Optional&lt;T&gt;.
    /// Ceiling: one property on one endpoint. Two sibling gaps share this shape and are NOT fixed here —
    /// PATCH /documents/{id} cannot move a document back to the top level, and GET /documents has no way
    /// to ask for "folderId is null" (Dashboard.tsx records both). Neither is one mechanism away: the
    /// documents body needs this same flag, but a GET filter is a query string where absent and null are
    /// genuinely the same value and only a sentinel token can separate them. Upgrade path: repeat this
    /// pattern on DocumentEndpoints.UpdateRequest, and add `folderId=none` to ListDocuments.
    /// </summary>
    public sealed class UpdateRequest
    {
        public string? Name { get; set; }

        private Guid? _parentId;
        public Guid? ParentId
        {
            get => _parentId;
            set { _parentId = value; HasParentId = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasParentId { get; private set; }
    }

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

    private static async Task<IResult> Create(CreateFolderRequest req, HttpContext ctx, EasyDocsDbContext db)
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
        db.Add(Audit.Event(orgId, null, CurrentUser.UserId(ctx.User), "folder.created", "folder", folder.Id.ToString(),
            new { name, parentId = req.ParentId }));
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
        if (req.HasParentId)
        {
            if (req.ParentId is { } pid)
            {
                if (pid == id) return Problem.Of(400, "Invalid parent", "A folder cannot be its own parent.");
                if (!await ExistsAsync(db, orgId, pid)) return Problem.Of(400, "Invalid parent", "parentId does not exist in your org.");
                if (await IsDescendantAsync(db, orgId, ancestor: id, candidate: pid))
                    return Problem.Of(400, "Invalid parent",
                        "That folder is inside this one; moving it there would leave both unreachable.");
            }
            folder.ParentId = req.ParentId; // explicit null == move to the root

            // Same pre-check Create needs, for the same reason: the unique index treats a NULL ParentId
            // as distinct, so it cannot catch a duplicate at the root. Without this, "move to root"
            // would be the one way to end up with two same-named folders side by side.
            if (folder.ParentId is null && await db.Folders.AnyAsync(f =>
                    f.OrgId == orgId && f.DeletedAt == null && f.ParentId == null
                    && f.Name == folder.Name && f.Id != id))
                return Problem.Of(409, "Duplicate name", "A folder with that name already exists here.");
        }

        db.Add(Audit.Event(orgId, null, CurrentUser.UserId(ctx.User), "folder.updated", "folder", id.ToString(),
            new { name = folder.Name, parentId = folder.ParentId }));
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
        db.Add(Audit.Event(orgId, null, CurrentUser.UserId(ctx.User), "folder.deleted", "folder", id.ToString(),
            new { mode = mode ?? "trash", promoted = mode == "promote_children" ? children.Count : 0 }));
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static Task<bool> ExistsAsync(EasyDocsDbContext db, Guid orgId, Guid id) =>
        db.Folders.AnyAsync(f => f.OrgId == orgId && f.DeletedAt == null && f.Id == id);

    /// <summary>
    /// Is <paramref name="candidate"/> inside <paramref name="ancestor"/>? Walks the candidate's parent
    /// chain upwards. Nothing else in this API can create a cycle, but this has to terminate even if one
    /// already exists in the data, so it also stops on a repeat.
    /// </summary>
    // ponytail: one query for the org's (id, parent) pairs, walked in memory. Ceiling: it reads every
    // folder in the org — a folder tree is a filing cabinet, not a dataset. A recursive CTE if one ever
    // has thousands.
    private static async Task<bool> IsDescendantAsync(EasyDocsDbContext db, Guid orgId, Guid ancestor, Guid candidate)
    {
        var parents = await db.Folders
            .Where(f => f.OrgId == orgId && f.DeletedAt == null)
            .ToDictionaryAsync(f => f.Id, f => f.ParentId);

        var seen = new HashSet<Guid>();
        for (Guid? at = candidate; at is { } cur && seen.Add(cur); at = parents.GetValueOrDefault(cur))
            if (cur == ancestor) return true;
        return false;
    }
}
