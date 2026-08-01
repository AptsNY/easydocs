using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public class WopiHostTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public WopiHostTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private static readonly byte[] BaseBytes = { 1, 2, 3, 4, 5 };

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"wopi-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return client;
    }

    // Register -> create doc -> upload base version -> mint session. Returns (sessionId, accessToken).
    private async Task<(Guid sid, string token)> MintSessionAsync(
        HttpClient c, string docName = "Lease", byte[]? bytes = null)
    {
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = docName }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var part = new ByteArrayContent(bytes ?? BaseBytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var form = new MultipartFormDataContent { { part, "file", "lease.docx" } };
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", form);
        up.EnsureSuccessStatusCode();
        var vid = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var mint = (await (await c.PostAsync($"/api/v1/versions/{vid}/sessions", null))
            .Content.ReadFromJsonAsync<MintDto>())!;
        return (mint.SessionId, mint.AccessToken);
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);
    private record MintDto(Guid SessionId, string AccessToken);
    private record CheckFileInfoDto(string BaseFileName, long Size, string UserId, bool UserCanWrite, string Version);

    [Fact]
    public async Task CheckFileInfo_returns_name_size_write_perm()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);

        var info = await _f.CreateClient()
            .GetFromJsonAsync<CheckFileInfoDto>($"/wopi/files/{sid}?access_token={token}");

        Assert.NotNull(info);
        Assert.Equal("Lease.docx", info!.BaseFileName);
        Assert.Equal(BaseBytes.Length, info.Size);
        Assert.True(info.UserCanWrite);
        Assert.False(string.IsNullOrEmpty(info.Version));
    }

    // The DTO test above proves nothing about casing: GetFromJsonAsync is case-insensitive, so it passed
    // happily while the shipped host emitted `baseFileName` and Collabora answered "Unauthorized WOPI
    // host". WOPI property names are protocol constants (spec §6), not house style, so assert the RAW
    // BYTES — this is the test that fails if anyone re-points these at the app's camelCase JSON policy.
    [Fact]
    public async Task CheckFileInfo_wire_json_is_WOPI_PascalCase_not_the_app_naming_policy()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);

        var raw = await _f.CreateClient().GetStringAsync($"/wopi/files/{sid}?access_token={token}");

        string[] wopiNames =
        [
            "BaseFileName", "Size", "OwnerId", "UserId", "UserFriendlyName", "UserCanWrite",
            "Version", "SupportsLocks", "SupportsUpdate", "SupportsGetLock",
        ];
        foreach (var name in wopiNames)
        {
            Assert.Contains($"\"{name}\"", raw);
            Assert.DoesNotContain($"\"{char.ToLowerInvariant(name[0])}{name[1..]}\"", raw);
        }
    }

    // Documents are named after the file they were ingested from ("… laundry lease.docx"), so appending
    // a literal ".docx" produced "… laundry lease.docx.docx" for the entire corpus. BaseFileName must
    // carry exactly one extension, and it must be the blob's REAL type — Collabora shows the name to the
    // user and picks its editor from the extension (spec §6, and the same R8 rule as the download name).
    [Theory]
    [InlineData("Lease", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "Lease.docx")]           // zip -> docx
    [InlineData("Lease.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "Lease.docx")]      // no double ext
    [InlineData("Lease.doc", new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, "Lease.doc")]
    [InlineData("Lease.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "Lease.pdf")]  // %PDF-
    [InlineData("Suite 4.2 Lease", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "Suite 4.2 Lease.docx")]
    public async Task CheckFileInfo_BaseFileName_has_exactly_one_correct_extension(
        string docName, byte[] bytes, string expected)
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c, docName, bytes);

        var info = await _f.CreateClient()
            .GetFromJsonAsync<CheckFileInfoDto>($"/wopi/files/{sid}?access_token={token}");

        Assert.Equal(expected, info!.BaseFileName);
    }

    [Fact]
    public async Task GetFile_streams_base_version_bytes()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);

        var resp = await _f.CreateClient().GetAsync($"/wopi/files/{sid}/contents?access_token={token}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(BaseBytes, await resp.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PutFile_creates_new_version_via_commit_save()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);
        var wopi = _f.CreateClient();

        // LOCK first (Collabora holds a lock while editing), then PutFile with the matching lock.
        var lockReq = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{sid}?access_token={token}");
        lockReq.Headers.Add("X-WOPI-Override", "LOCK");
        lockReq.Headers.Add("X-WOPI-Lock", "L1");
        (await wopi.SendAsync(lockReq)).EnsureSuccessStatusCode();

        var edited = new byte[] { 9, 8, 7, 6 };
        var putReq = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{sid}/contents?access_token={token}")
        {
            Content = new ByteArrayContent(edited),
        };
        putReq.Headers.Add("X-WOPI-Lock", "L1");
        var put = await wopi.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var session = await db.EditSessions.SingleAsync(s => s.Id == sid);
        Assert.NotNull(session.LastCommittedSha);
        // Two versions on the doc now: the uploaded base + the WOPI PutFile.
        Assert.Equal(2, await db.Versions.CountAsync(v => v.DocumentId == session.DocumentId));
    }

    [Fact]
    public async Task Lock_unlock_lifecycle_and_conflict()
    {
        var c = await AuthedClientAsync();
        var (sid, token) = await MintSessionAsync(c);
        var wopi = _f.CreateClient();

        async Task<HttpResponseMessage> Op(string @override, string? lockVal)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{sid}?access_token={token}");
            req.Headers.Add("X-WOPI-Override", @override);
            if (lockVal is not null) req.Headers.Add("X-WOPI-Lock", lockVal);
            return await wopi.SendAsync(req);
        }

        Assert.Equal(HttpStatusCode.OK, (await Op("LOCK", "L1")).StatusCode);

        var get = await Op("GET_LOCK", null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("L1", get.Headers.GetValues("X-WOPI-Lock").Single());

        var conflict = await Op("LOCK", "L2");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("L1", conflict.Headers.GetValues("X-WOPI-Lock").Single());

        Assert.Equal(HttpStatusCode.OK, (await Op("UNLOCK", "L1")).StatusCode);
    }

    [Fact]
    public async Task Invalid_access_token_401()
    {
        var c = await AuthedClientAsync();
        var (sid, _) = await MintSessionAsync(c);

        var resp = await _f.CreateClient().GetAsync($"/wopi/files/{sid}?access_token=garbage");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
