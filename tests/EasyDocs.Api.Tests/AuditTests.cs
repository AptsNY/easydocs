using System.Net;
using System.Net.Http.Json;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// GET /api/v1/documents/{id}/audit (spec §10.1, §11) — the per-document append-only trail.
public class AuditTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public AuditTests(ApiFactory f) => _f = f;

    private record AuditDto(AuditItem[] Items, string? NextCursor);
    private record AuditItem(Guid Id, string Action, Guid? ActorUserId, string? TargetType, string? TargetId, DateTimeOffset CreatedAt);

    private static async Task<AuditDto> TrailAsync(HttpClient c, Guid docId, int? limit = null, string? cursor = null)
    {
        var url = $"/api/v1/documents/{docId}/audit";
        var query = new List<string>();
        if (limit is not null) query.Add($"limit={limit}");
        if (cursor is not null) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        if (query.Count > 0) url += "?" + string.Join("&", query);

        var res = await c.GetAsync(url);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AuditDto>())!;
    }

    [Fact]
    public async Task Membership_changes_are_recorded_with_actor_and_target()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);

        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Editor" });

        var trail = await TrailAsync(a.Client, docId);
        var added = trail.Items.Single(e => e.Action == "member.added");
        Assert.Equal(a.UserId, added.ActorUserId);
        Assert.Equal("user", added.TargetType);
        Assert.Equal(b.UserId.ToString(), added.TargetId);
    }

    [Fact]
    public async Task Trail_is_newest_first_and_cursor_paginated()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var b = await _f.SeedOrgUserAsync(a.OrgId);

        // Six distinct mutations => six audited events, each its own request so timestamps are distinct.
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Editor" });
        await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{b.UserId}", new { role = "Viewer" });
        await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}/members/{b.UserId}", new { role = "Editor" });
        await a.Client.DeleteAsync($"/api/v1/documents/{docId}/members/{b.UserId}");
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = b.Email, role = "Viewer" });
        await a.Client.DeleteAsync($"/api/v1/documents/{docId}/members/{b.UserId}");

        var all = await TrailAsync(a.Client, docId, limit: 100);
        Assert.True(all.Items.Length >= 6, $"expected >= 6 events, saw {all.Items.Length}");
        Assert.Null(all.NextCursor);

        // Newest first.
        var times = all.Items.Select(i => i.CreatedAt).ToArray();
        Assert.Equal(times.OrderByDescending(t => t).ToArray(), times);

        // Walk the cursor and confirm it reproduces the same sequence with no gaps or repeats.
        var walked = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 20; guard++)
        {
            var page = await TrailAsync(a.Client, docId, limit: 2, cursor: cursor);
            Assert.True(page.Items.Length <= 2);
            walked.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(all.Items.Select(i => i.Id).ToArray(), walked.ToArray());
        Assert.Equal(walked.Distinct().Count(), walked.Count);
    }

    [Fact]
    public async Task Any_member_may_read_the_trail()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();
        var viewer = await _f.SeedOrgUserAsync(a.OrgId);
        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email = viewer.Email, role = "Viewer" });

        var res = await viewer.Client.GetAsync($"/api/v1/documents/{docId}/audit");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Non_member_403_and_cross_org_404()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync();

        var stranger = await _f.SeedOrgUserAsync(a.OrgId);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.Client.GetAsync($"/api/v1/documents/{docId}/audit")).StatusCode);

        var other = await _f.RegisterAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/documents/{docId}/audit")).StatusCode);
    }

    // Spec §11 / the M3 exit checklist: "every mutation audited". Walks the document lifecycle and
    // asserts each mutating call left a row behind.
    [Fact]
    public async Task Every_document_mutation_lands_in_the_trail()
    {
        var a = await _f.RegisterAsync();
        var docId = await a.Client.CreateDocAsync("Audited");
        var (v1, _) = await a.Client.UploadAsync(docId);

        await a.Client.PatchAsJsonAsync($"/api/v1/documents/{docId}", new { name = "Renamed" });
        await a.Client.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 0, minor = 0, rev = 3 });
        await a.Client.PatchAsJsonAsync($"/api/v1/versions/{v1}", new { name = "Draft label" });

        var publish = await a.Client.PostAsJsonAsync($"/api/v1/versions/{v1}/publish", new { kind = "minor" });
        publish.EnsureSuccessStatusCode();

        var approvals = await a.Client.PostAsJsonAsync($"/api/v1/versions/{v1}/approvals",
            new { approverIds = new[] { a.UserId } });
        approvals.EnsureSuccessStatusCode();
        var approvalId = (await approvals.Content.ReadFromJsonAsync<ApprovalItem[]>())!.Single().Id;
        await a.Client.PostAsJsonAsync($"/api/v1/approvals/{approvalId}:respond",
            new { decision = "approved", comment = "ok" });

        var share = await a.Client.PostAsJsonAsync($"/api/v1/versions/{v1}/share-links", new { expiresAt = (DateTimeOffset?)null });
        share.EnsureSuccessStatusCode();
        var shareId = await ShareLinkIdAsync(docId);
        await a.Client.DeleteAsync($"/api/v1/share-links/{shareId}");

        await a.Client.PostAsync($"/api/v1/versions/{v1}/revert", null);
        await a.Client.DeleteAsync($"/api/v1/documents/{docId}");
        await a.Client.PostAsync($"/api/v1/documents/{docId}:restore", null);

        var actions = (await TrailAsync(a.Client, docId, limit: 100)).Items.Select(i => i.Action).ToHashSet();
        string[] expected =
        [
            "document.created", "version.created", "document.updated", "version_counter.set",
            "version.named", "version.published", "approval.requested", "approval.responded",
            "share_link.created", "share_link.revoked", "version.reverted",
            "document.trashed", "document.restored",
        ];
        var missing = expected.Where(e => !actions.Contains(e)).ToArray();
        Assert.Empty(missing);
    }

    private record ApprovalItem(Guid Id);

    private async Task<Guid> ShareLinkIdAsync(Guid docId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocs.Api.Data.EasyDocsDbContext>();
        return await db.ShareLinks
            .Where(s => db.Versions.Any(v => v.Id == s.VersionId && v.DocumentId == docId))
            .Select(s => s.Id)
            .SingleAsync();
    }

    [Fact]
    public async Task Trail_is_scoped_to_one_document()
    {
        var a = await _f.RegisterAsync();
        var docA = await a.Client.CreateDocAsync("A");
        var docB = await a.Client.CreateDocAsync("B");
        var b = await _f.SeedOrgUserAsync(a.OrgId);

        await a.Client.PostAsJsonAsync($"/api/v1/documents/{docA}/members", new { email = b.Email, role = "Editor" });

        Assert.Contains((await TrailAsync(a.Client, docA)).Items, e => e.Action == "member.added");
        Assert.DoesNotContain((await TrailAsync(a.Client, docB)).Items, e => e.Action == "member.added");
    }
}
