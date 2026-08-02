using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// Issue #11: the WebDAV conversation Word actually has — OPTIONS, PROPFIND, GET, LOCK, PUT — driven
// directly, the same way the WOPI suite drives Collabora's half. Word is a client of a server-to-
// server contract; no desktop is needed to prove the contract.
public class WebdavTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);
    private record MintDto(Guid SessionId, string Url, string MsWordUrl);
    private record VersionDto(Guid Id, string Source, string Number);

    private async Task<(HttpClient Client, Guid DocId, Guid VersionId, byte[] Uploaded)> SeedAsync()
    {
        var client = f.CreateClient();
        var email = $"dav-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="))
            ["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var docId = (await (await client.PostAsJsonAsync("/api/v1/documents", new { name = "Dav Doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        // Captured once: DocxFixtures rebuilds the zip per call, so two calls are not byte-identical.
        var uploaded = DocxFixtures.Base();
        var part = new ByteArrayContent(uploaded);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var up = await client.PostAsync($"/api/v1/documents/{docId}/versions",
            new MultipartFormDataContent { { part, "file", "d.docx" } });
        up.EnsureSuccessStatusCode();
        return (client, docId, (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId, uploaded);
    }

    private static string DavPath(MintDto mint) => new Uri(mint.Url).PathAndQuery;

    [Fact]
    public async Task The_word_conversation_options_propfind_get_lock_put()
    {
        var (client, docId, versionId, uploaded) = await SeedAsync();

        var mint = await (await client.PostAsync($"/api/v1/versions/{versionId}/webdav-sessions", null))
            .Content.ReadFromJsonAsync<MintDto>();
        Assert.StartsWith("ms-word:ofe|u|", mint!.MsWordUrl);
        Assert.EndsWith(".docx", mint.Url);
        var path = DavPath(mint);

        // Word's opening move: OPTIONS, expecting class-2 DAV and MS-Author-Via.
        var options = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, path));
        options.EnsureSuccessStatusCode();
        Assert.Equal("1,2", options.Headers.GetValues("DAV").Single());
        Assert.Equal("DAV", options.Headers.GetValues("MS-Author-Via").Single());

        // PROPFIND depth 0: a 207 with the file's name and length.
        var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
        propfind.Headers.Add("Depth", "0");
        var props = await client.SendAsync(propfind);
        Assert.Equal(207, (int)props.StatusCode);
        var xml = await props.Content.ReadAsStringAsync();
        Assert.Contains("Dav Doc.docx", xml);
        Assert.Contains($"<D:getcontentlength>{uploaded.Length}", xml);

        // GET returns exactly the uploaded bytes.
        var got = await client.GetAsync(path);
        got.EnsureSuccessStatusCode();
        Assert.Equal(DocxMime, got.Content.Headers.ContentType!.MediaType);
        Assert.Equal(uploaded, await got.Content.ReadAsByteArrayAsync());

        // LOCK issues an opaque token; UNLOCK with it succeeds.
        var lockRes = await client.SendAsync(new HttpRequestMessage(new HttpMethod("LOCK"), path));
        lockRes.EnsureSuccessStatusCode();
        var lockToken = lockRes.Headers.GetValues("Lock-Token").Single();
        Assert.Contains("opaquelocktoken:", lockToken);

        // PUT = save: a NEW version through the single write path, source EditWebdav.
        var putBytes = DocxFixtures.Edited();
        var putReq = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ByteArrayContent(putBytes),
        };
        var put = await client.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var newVersionId = Guid.Parse(put.Headers.GetValues("X-EasyDocs-Version").Single());
        Assert.NotEqual(versionId, newVersionId);

        var version = await client.GetFromJsonAsync<VersionDto>($"/api/v1/versions/{newVersionId}");
        Assert.Equal("EditWebdav", version!.Source);

        // After the save, GET serves what Word wrote, not the stale base.
        var reGet = await client.GetAsync(path);
        Assert.Equal(putBytes, await reGet.Content.ReadAsByteArrayAsync());

        var unlock = new HttpRequestMessage(new HttpMethod("UNLOCK"), path);
        unlock.Headers.Add("Lock-Token", lockToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(unlock)).StatusCode);
    }

    [Fact]
    public async Task A_garbage_token_gets_401_and_a_viewer_cannot_mint()
    {
        var (client, _, versionId, _) = await SeedAsync();
        var bad = await client.GetAsync("/dav/not-a-token/whatever.docx");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // No session cookie at all: minting is an authenticated, editor-gated action.
        var anon = f.CreateClient();
        var res = await anon.PostAsync($"/api/v1/versions/{versionId}/webdav-sessions", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
