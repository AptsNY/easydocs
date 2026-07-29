using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// /s/{token} serves two audiences: a browser (wants the SPA landing page) and an API client (wants
// JSON). Content negotiation rather than a second route, so the link in an email is the link in the
// API and there is only one token surface to audit.
public class ShareLandingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ShareLandingTests(ApiFactory f) => _f = f;

    private record ShareLink(string Token, string Url);

    private async Task<(string Url, Guid DocId)> ShareUrlAsync()
    {
        var acct = await _f.RegisterAsync();
        var docId = await acct.Client.CreateDocAsync("Shared");
        var (vid, _) = await acct.Client.UploadAsync(docId, DocxFixtures.Base());
        var res = await acct.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/share-links", new { });
        res.EnsureSuccessStatusCode();
        return ((await res.Content.ReadFromJsonAsync<ShareLink>())!.Url, docId);
    }

    [Fact]
    public async Task A_browser_gets_the_spa_shell_and_an_api_client_gets_json()
    {
        var (url, _) = await ShareUrlAsync();
        var anon = _f.CreateClient();

        var browser = new HttpRequestMessage(HttpMethod.Get, url);
        browser.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var html = await anon.SendAsync(browser);
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\">", await html.Content.ReadAsStringAsync());

        var api = new HttpRequestMessage(HttpMethod.Get, url);
        api.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var json = await anon.SendAsync(api);
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
        Assert.Contains("downloadUrl", await json.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_html_shell_does_not_audit_or_count_a_view()
    {
        // The SPA immediately re-requests the same URL as JSON. If both hits audited, every share view
        // would be double-counted and the audit trail would gain a phantom read.
        var (url, docId) = await ShareUrlAsync();
        var anon = _f.CreateClient();

        var browser = new HttpRequestMessage(HttpMethod.Get, url);
        browser.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        (await anon.SendAsync(browser)).EnsureSuccessStatusCode();

        // Scoped to this test's own document: ApiFactory is an IClassFixture, so its database is
        // shared across every [Fact] in this class, not reset per-test. An unscoped query over ALL
        // share_link.viewed rows would pick up audit rows left behind by sibling tests' own JSON hits.
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.EasyDocsDbContext>();
        Assert.Empty(db.AuditEvents.Where(a => a.Action == "share_link.viewed" && a.DocumentId == docId).ToList());
    }

    [Fact]
    public async Task An_unknown_token_still_serves_the_shell_to_a_browser()
    {
        // Same page for valid and invalid: the SPA shows "link not found" from the JSON 404. Serving a
        // bare 404 here would make the shell path a token oracle.
        var anon = _f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/s/not-a-real-token");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await anon.GetAsync("/s/not-a-real-token")).StatusCode); // no Accept -> JSON path
    }
}
