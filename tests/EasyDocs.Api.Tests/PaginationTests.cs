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
        var when = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var decoded = Pagination.Decode(Pagination.EncodeTime(Pagination.CreatedTag, when, id));
        Assert.NotNull(decoded);
        Assert.Equal(Pagination.CreatedTag, decoded!.Tag);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(when, Pagination.AsTime(decoded));

        // A null cursor (no query parameter at all) is not malformed input; it means "page one."
        Assert.Null(Pagination.Decode(null));
    }

    [Fact]
    public void A_time_cursor_round_trips()
    {
        var when = new DateTimeOffset(2026, 8, 21, 12, 34, 56, TimeSpan.Zero);
        var id = Guid.NewGuid();

        var decoded = Pagination.Decode(Pagination.EncodeTime(7, when, id));

        Assert.NotNull(decoded);
        Assert.Equal(7, decoded!.Tag);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(when, Pagination.AsTime(decoded));
    }

    // A name key is variable-length and may be multibyte, which is the whole reason the payload
    // carries no length field — everything after the tag and before the trailing 16 bytes IS the key.
    [Fact]
    public void A_text_cursor_round_trips_including_multibyte_names()
    {
        var id = Guid.NewGuid();
        const string name = "bail à loyer — 賃貸借契約";

        var decoded = Pagination.Decode(Pagination.EncodeText(2, name, id));

        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Tag);
        Assert.Equal(id, decoded.Id);
        Assert.Equal(name, Pagination.AsText(decoded));
    }

    // A cursor is worthless if a client's garbage throws instead of restarting the list: Decode
    // returning null means "no WHERE clause", which means page one.
    [Theory]
    [InlineData("")]
    [InlineData("not-base64url-!!!")]
    [InlineData("AAAA")] // decodes, but too short to hold a tag and a Guid
    public void An_unusable_cursor_decodes_to_null_rather_than_throwing(string cursor)
    {
        Assert.Null(Pagination.Decode(cursor));
    }

    // A zero-length key is legal (an empty document name lower-cases to ""), and must not be
    // mistaken for a truncated payload.
    [Fact]
    public void An_empty_text_key_is_a_valid_cursor()
    {
        var id = Guid.NewGuid();

        var decoded = Pagination.Decode(Pagination.EncodeText(2, "", id));

        Assert.NotNull(decoded);
        Assert.Equal("", Pagination.AsText(decoded!));
        Assert.Equal(id, decoded!.Id);
    }
}
