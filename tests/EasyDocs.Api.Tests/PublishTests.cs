using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class PublishTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PublishTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);
    private record PublishDto(Guid VersionId, int Major, int Minor, int Revision, string Kind);
    private record PublicationDto(Guid VersionId, int Major, int Minor, int Revision, string? Name, Guid PublishedBy, string? PublishedByName, DateTimeOffset PublishedAt, string Kind);
    private record PublicationPage(List<PublicationDto> Items, string? NextCursor);

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"pub-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "P", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var body = await reg.Content.ReadFromJsonAsync<RegisterDto>();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, body!.Id);
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    private async Task<Guid> CreateDocAsync(HttpClient c)
        => (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

    // Distinct bytes each call so sessionless uploads never dedupe against the head.
    private async Task<UploadDto> UploadAsync(HttpClient c, Guid docId, byte marker)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { marker, 9, 9 }));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!;
    }

    [Fact]
    public async Task Publish_minor_renumbers_selected_version_and_advances_counter()
    {
        var (c, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        await UploadAsync(c, docId, 1);            // 0.0.1
        var v2 = await UploadAsync(c, docId, 2);   // 0.0.2  <- publish this one
        await UploadAsync(c, docId, 3);            // 0.0.3

        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v2.VersionId}/publish", new { kind = "minor" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pub = (await res.Content.ReadFromJsonAsync<PublishDto>())!;
        Assert.Equal((0, 1, 0), (pub.Major, pub.Minor, pub.Revision));

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var version = await db.Versions.FirstAsync(v => v.Id == v2.VersionId);
            Assert.Equal((0, 1, 0), (version.Major, version.Minor, version.Revision));
            Assert.Equal("minor", version.PublishedKind);
            Assert.Equal(userId, version.PublishedBy);
            Assert.NotNull(version.PublishedAt);
            var doc = await db.Documents.FirstAsync(d => d.Id == docId);
            Assert.Equal((0, 1, 0), (doc.VersionCounterMajor, doc.VersionCounterMinor, doc.VersionCounterRev));
        }

        // R6: the next draft continues from the published number -> 0.1.1
        var next = await UploadAsync(c, docId, 4);
        Assert.Equal((0, 1, 1), (next.Major, next.Minor, next.Revision));
    }

    [Fact]
    public async Task Publish_major_bumps_major()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        var v1 = await UploadAsync(c, docId, 1);   // 0.0.1
        // Get counter to 0.1.x first with a minor publish.
        (await c.PostAsJsonAsync($"/api/v1/versions/{v1.VersionId}/publish", new { kind = "minor" })).EnsureSuccessStatusCode();
        var v2 = await UploadAsync(c, docId, 2);   // 0.1.1

        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v2.VersionId}/publish", new { kind = "major" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var pub = (await res.Content.ReadFromJsonAsync<PublishDto>())!;
        Assert.Equal((1, 0, 0), (pub.Major, pub.Minor, pub.Revision));

        var next = await UploadAsync(c, docId, 3);
        Assert.Equal((1, 0, 1), (next.Major, next.Minor, next.Revision));
    }

    [Fact]
    public async Task Publish_requires_editor()
    {
        var (c, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);
        var v1 = await UploadAsync(c, docId, 1);

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var m = await db.DocumentMembers.SingleAsync(x => x.DocumentId == docId && x.UserId == userId);
            m.Role = DocRole.Viewer;
            await db.SaveChangesAsync();
        }

        var res = await c.PostAsJsonAsync($"/api/v1/versions/{v1.VersionId}/publish", new { kind = "minor" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Publications_lists_only_published_versions()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        var v1 = await UploadAsync(c, docId, 1);
        var v2 = await UploadAsync(c, docId, 2);
        await UploadAsync(c, docId, 3); // unpublished draft — must not appear

        (await c.PostAsJsonAsync($"/api/v1/versions/{v1.VersionId}/publish", new { kind = "minor", name = "First" })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync($"/api/v1/versions/{v2.VersionId}/publish", new { kind = "major", name = "Second" })).EnsureSuccessStatusCode();

        var list = (await c.GetFromJsonAsync<PublicationPage>($"/api/v1/documents/{docId}/publications"))!.Items;
        Assert.Equal(2, list!.Count);
        // Newest first: v2 (major) published last.
        Assert.Equal(v2.VersionId, list[0].VersionId);
        Assert.Equal("major", list[0].Kind);
        Assert.Equal("Second", list[0].Name);
        Assert.Equal(v1.VersionId, list[1].VersionId);
        Assert.Equal("minor", list[1].Kind);
    }

    // The Major Versions list is a read surface, so it resolves display names like every other one
    // (version rows, the audit trail, approvals). Two publishers on one page: the page is fetched first
    // and then ONE AuthorNames lookup covers both, so this also pins the absence of an N+1.
    [Fact]
    public async Task Publications_resolve_the_publisher_display_name()
    {
        var owner = await _f.RegisterAsync();                        // DisplayName "U"
        var docId = await owner.Client.CreateDocAsync();
        var second = await _f.SeedOrgUserAsync(owner.OrgId);         // DisplayName "Seed"
        (await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = second.Email, role = "Editor" })).EnsureSuccessStatusCode();

        var (v1, _) = await owner.Client.UploadAsync(docId, new byte[] { 1, 9, 9 });
        var (v2, _) = await owner.Client.UploadAsync(docId, new byte[] { 2, 9, 9 });
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{v1}/publish", new { kind = "minor" })).EnsureSuccessStatusCode();
        (await second.Client.PostAsJsonAsync($"/api/v1/versions/{v2}/publish", new { kind = "major" })).EnsureSuccessStatusCode();

        var list = (await owner.Client.GetFromJsonAsync<PublicationPage>($"/api/v1/documents/{docId}/publications"))!.Items;
        Assert.Equal(2, list.Count);

        var byVersion = list.ToDictionary(p => p.VersionId);
        Assert.Equal("U", byVersion[v1].PublishedByName);
        Assert.Equal("Seed", byVersion[v2].PublishedByName);
        // The raw id is still there — the name is additional, not a replacement.
        Assert.Equal(owner.UserId, byVersion[v1].PublishedBy);
        Assert.Equal(second.UserId, byVersion[v2].PublishedBy);
    }
}
