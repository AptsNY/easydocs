using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class ShareLinkTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ShareLinkTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"sl-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record ShareDto(string Token, string Url);

    private static async Task<Guid> CreateDocAsync(HttpClient c, string name = "Doc")
    {
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;
    }

    private static async Task<UploadDto> UploadAsync(HttpClient c, Guid docId, byte[] bytes)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(bytes));
        up.EnsureSuccessStatusCode();
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!;
    }

    private static string Sha256Hex(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [Fact]
    public async Task Create_share_link_returns_token_once()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, new byte[] { 1, 2, 3 });

        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/share-links", new { });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ShareDto>())!;
        Assert.False(string.IsNullOrWhiteSpace(dto.Token));
        Assert.Equal($"/s/{dto.Token}", dto.Url);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var link = await db.ShareLinks.SingleAsync(x => x.VersionId == v.VersionId);
        // DB stores only the hash — never the plaintext token.
        Assert.NotEqual(dto.Token, link.TokenHash);
        Assert.Equal(Sha256Hex(dto.Token), link.TokenHash);
        Assert.Equal(0, link.ViewCount);
    }

    [Fact]
    public async Task Public_get_serves_version_and_increments_view_count_and_audits()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var bytes = new byte[] { 9, 8, 7, 6 };
        var v = await UploadAsync(c, docId, bytes);

        var share = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareDto>())!;

        // Public: no auth header.
        var anon = _f.CreateClient();
        var meta = await anon.GetAsync($"/s/{share.Token}");
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        var body = await meta.Content.ReadAsStringAsync();
        Assert.Contains("Master Lease", body);
        Assert.Contains("0.0.1", body);

        // Downloadable without membership.
        var dl = await anon.GetAsync($"/s/{share.Token}/download");
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Equal(bytes, await dl.Content.ReadAsByteArrayAsync());

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var link = await db.ShareLinks.SingleAsync(x => x.VersionId == v.VersionId);
        Assert.True(link.ViewCount >= 1);
        var audited = await db.AuditEvents.AnyAsync(a =>
            a.Action == "share_link.viewed" && a.TargetType == "version" && a.TargetId == v.VersionId.ToString());
        Assert.True(audited);
    }

    [Fact]
    public async Task Revoked_or_expired_token_404()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v = await UploadAsync(c, docId, new byte[] { 1 });

        var share = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareDto>())!;

        Guid linkId;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            linkId = await db.ShareLinks.Where(x => x.VersionId == v.VersionId).Select(x => x.Id).SingleAsync();
        }

        var del = await c.DeleteAsync($"/api/v1/share-links/{linkId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var anon = _f.CreateClient();
        var afterRevoke = await anon.GetAsync($"/s/{share.Token}");
        Assert.Equal(HttpStatusCode.NotFound, afterRevoke.StatusCode);

        // An expired link is also 404.
        var v2 = await UploadAsync(c, docId, new byte[] { 2 });
        var expired = (await (await c.PostAsJsonAsync($"/api/v1/versions/{v2.VersionId}/share-links",
            new { expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) })).Content.ReadFromJsonAsync<ShareDto>())!;
        var expiredGet = await anon.GetAsync($"/s/{expired.Token}");
        Assert.Equal(HttpStatusCode.NotFound, expiredGet.StatusCode);
    }

    // Until M5 the row id was never exposed anywhere, so DELETE /api/v1/share-links/{id} existed but no
    // client could reach it: a shared document could not be un-shared (spec §11).
    [Fact]
    public async Task Share_links_are_listable_per_document_so_revocation_is_reachable()
    {
        var c = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Listable");
        var v = await UploadAsync(c, docId, new byte[] { 1 });
        await c.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/share-links", new { });

        var res = await c.GetAsync($"/api/v1/documents/{docId}/share-links");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var raw = await res.Content.ReadAsStringAsync();
        // Neither the raw token (unrecoverable) nor the stored hash: a revoke decision needs neither.
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);

        var page = (await res.Content.ReadFromJsonAsync<PagedShareLinks>())!;
        var row = Assert.Single(page.Items);
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(v.VersionId, row.VersionId);
        Assert.Equal("0.0.1", row.VersionNumber);
        Assert.Equal("D", row.CreatedByName);
        Assert.Null(row.RevokedAt);
        Assert.Equal(0, row.ViewCount);

        // The id read off the list drives the existing revoke route, end to end.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/v1/share-links/{row.Id}")).StatusCode);

        // A revoked link stays listed, flagged — "did I revoke that?" is a question the list must answer.
        var after = (await c.GetFromJsonAsync<PagedShareLinks>($"/api/v1/documents/{docId}/share-links"))!;
        Assert.NotNull(Assert.Single(after.Items).RevokedAt);
    }

    [Fact]
    public async Task Listing_share_links_requires_document_membership()
    {
        var owner = await AuthedClientAsync();
        var docId = await CreateDocAsync(owner);
        await UploadAsync(owner, docId, new byte[] { 1 });

        var stranger = await AuthedClientAsync();
        var res = await stranger.GetAsync($"/api/v1/documents/{docId}/share-links");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // cross-org: no existence leak
    }

    private record ShareLinkRow(
        Guid Id, Guid VersionId, string VersionNumber, Guid CreatedBy, string CreatedByName,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt, int ViewCount);
    private record PagedShareLinks(ShareLinkRow[] Items, string? NextCursor);

    [Fact]
    public async Task Create_requires_membership()
    {
        var owner = await AuthedClientAsync();
        var docId = await CreateDocAsync(owner);
        var v = await UploadAsync(owner, docId, new byte[] { 1 });

        // A different user (different org) is not a member -> no info leak.
        var stranger = await AuthedClientAsync();
        var res = await stranger.PostAsJsonAsync($"/api/v1/versions/{v.VersionId}/share-links", new { });
        Assert.Contains(res.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }
}
