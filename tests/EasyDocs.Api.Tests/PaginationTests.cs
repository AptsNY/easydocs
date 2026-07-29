using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Api;
using EasyDocs.Api.Tests;

public class PaginationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public PaginationTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = $"pg-{Guid.NewGuid():N}@example.com", displayName = "P", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
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
        return new MultipartFormDataContent { { part, "file", "f.docx" } };
    }

    private record CreateDto(Guid Id);
    private record DocItem(Guid Id, string Name, Guid? FolderId);
    private record VersionItem(Guid Id, int Major, int Minor, int Revision);
    private record Page<T>(List<T> Items, string? NextCursor);

    [Fact]
    public async Task Documents_list_paginates_with_cursor()
    {
        var c = await AuthedClientAsync();
        var folderId = (await (await c.PostAsJsonAsync("/api/v1/folders", new { name = "F" }))
            .Content.ReadFromJsonAsync<CreateDto>())!.Id;

        var created = new HashSet<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var doc = await (await c.PostAsJsonAsync("/api/v1/documents", new { name = $"Doc {i}", folderId }))
                .Content.ReadFromJsonAsync<CreateDto>();
            created.Add(doc!.Id);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/v1/documents?folderId={folderId}&limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await c.GetFromJsonAsync<Page<DocItem>>(url);
            Assert.True(page!.Items.Count <= 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor); // exhausted
        Assert.Equal(seen.Count, seen.Distinct().Count()); // no item twice
        Assert.Equal(created, seen.ToHashSet()); // all returned
    }

    [Fact]
    public async Task Versions_list_paginates()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "V" }))
            .Content.ReadFromJsonAsync<CreateDto>())!.Id;

        for (var i = 0; i < 4; i++)
            (await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(new byte[] { (byte)(i + 1), 9, 9 }))).EnsureSuccessStatusCode();

        var seen = new List<Guid>();
        string? cursor = null;
        for (var guard = 0; guard < 10; guard++)
        {
            var url = $"/api/v1/documents/{docId}/versions?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await c.GetFromJsonAsync<Page<VersionItem>>(url);
            Assert.True(page!.Items.Count <= 2);
            seen.AddRange(page.Items.Select(x => x.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        Assert.Null(cursor);
        Assert.Equal(4, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    [Fact]
    public void Cursor_is_opaque_and_roundtrips()
    {
        var key = (DateTimeOffset.UtcNow, Guid.NewGuid());
        var encoded = Pagination.Encode(key);
        var decoded = Pagination.Decode(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(key, decoded!.Value);

        // Malformed cursors are ignored, never throw.
        Assert.Null(Pagination.Decode(null));
        Assert.Null(Pagination.Decode(""));
        Assert.Null(Pagination.Decode("!!! not base64 !!!"));
        Assert.Null(Pagination.Decode("YWJj")); // valid base64url but wrong byte length
    }
}
