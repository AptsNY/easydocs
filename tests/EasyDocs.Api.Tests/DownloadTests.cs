using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class DownloadTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DownloadTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<(HttpClient client, string orgSlug)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"dl-{Guid.NewGuid():N}@example.com";
        var orgName = $"Org-{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var org = await db.Organizations.OrderByDescending(o => o.CreatedAt).FirstAsync(o => o.Name == orgName);
        return (client, org.Slug);
    }

    private static MultipartFormDataContent Docx(byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        return new MultipartFormDataContent { { part, "file", "d.docx" } };
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId, int Major, int Minor, int Revision);

    private static async Task<Guid> CreateDocAsync(HttpClient c, string name)
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

    [Fact]
    public async Task Download_docx_has_R8_filename()
    {
        var (c, slug) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var bytes = new byte[] { 10, 20, 30, 40 };
        var v = await UploadAsync(c, docId, bytes);
        Assert.Equal((0, 0, 1), (v.Major, v.Minor, v.Revision));

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download?format=docx");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal($"{slug}__Master_Lease-v0.0.1.docx", resp.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var body = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, body);
    }

    [Fact]
    public async Task Download_pdf_unpublished_returns_409()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Master Lease");
        var v = await UploadAsync(c, docId, new byte[] { 1, 2, 3 });

        var resp = await c.GetAsync($"/api/v1/versions/{v.VersionId}/download?format=pdf");
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Manual_counter_override_then_next_draft_follows()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Counter Doc");

        var set0 = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 0, minor = 0, rev = 0 });
        Assert.Equal(HttpStatusCode.OK, set0.StatusCode);
        var v1 = await UploadAsync(c, docId, new byte[] { 1 });
        Assert.Equal((0, 0, 1), (v1.Major, v1.Minor, v1.Revision));

        var set2 = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 2, minor = 5, rev = 9 });
        Assert.Equal(HttpStatusCode.OK, set2.StatusCode);
        var v2 = await UploadAsync(c, docId, new byte[] { 2 }); // distinct bytes -> new version
        Assert.Equal((2, 5, 10), (v2.Major, v2.Minor, v2.Revision));
    }

    [Fact]
    public async Task Manual_counter_negative_400()
    {
        var (c, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c, "Counter Doc");

        var resp = await c.PutAsJsonAsync($"/api/v1/documents/{docId}/version-counter", new { major = 0, minor = 0, rev = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
