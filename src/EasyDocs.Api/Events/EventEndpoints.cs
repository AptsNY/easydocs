using EasyDocs.Api.Auth;
using EasyDocs.Api.Common;
using EasyDocs.Api.Data;

namespace EasyDocs.Api.Events;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/documents/{id:guid}/events", Stream).RequireAuthorization();
    }

    private static async Task Stream(Guid id, HttpContext ctx, EasyDocsDbContext db, EventBus bus)
    {
        // Authorize BEFORE streaming — never open the stream to a non-member.
        var (access, _) = await DocumentAuthorization.ResolveAsync(
            db, CurrentUser.OrgId(ctx.User), CurrentUser.UserId(ctx.User), id, ctx.RequestAborted);
        if (access is AccessResult.NotFound)
        {
            await Problem.Of(404, "Not found", "Document not found.").ExecuteAsync(ctx);
            return;
        }
        if (access is AccessResult.Forbidden)
        {
            await Problem.Of(403, "Forbidden", "You are not a member of this document.").ExecuteAsync(ctx);
            return;
        }

        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // don't let nginx buffer the stream

        var ct = ctx.RequestAborted;
        // Prime the stream with an SSE comment: opens the client's read stream immediately so it isn't
        // blocked waiting for the first real event (a bare header flush ships no body bytes to unblock it).
        await ctx.Response.WriteAsync(": hello\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        try
        {
            await foreach (var (type, json) in bus.Subscribe(id, ct))
            {
                await ctx.Response.WriteAsync($"event: {type}\ndata: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
    }
}
