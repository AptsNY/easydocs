using System.Net;
using EasyDocs.Api.Editing;
using Microsoft.Extensions.Configuration;

namespace EasyDocs.Api.Tests;

// The two-origin Collabora shape (Cloud Run, 2026-09-21): the APP fetches /hosting/discovery from
// COLLABORA_URL, the BROWSER loads the editor from COLLABORA_PUBLIC_URL, and coolwsd builds urlsrc
// from whichever host it was reached on. Nothing pinned the re-origining that bridges them, so a
// regression would hand browsers an editor URL they cannot fetch — the exact failure collabora.spec.ts
// documents surviving four milestones.
public class CollaboraDiscoveryTests
{
    private const string Discovery = """
        <wopi-discovery><net-zone name="external-http"><app name="writer">
          <action ext="docx" name="edit" urlsrc="http://collabora:9980/browser/abc123/cool.html?"/>
        </app></net-zone></wopi-discovery>
        """;

    private sealed class Canned(string body, Action<HttpRequestMessage> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            seen(req);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static CollaboraDiscovery Build(Dictionary<string, string?> cfg, Action<HttpRequestMessage>? seen = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(),
            new HttpClient(new Canned(Discovery, seen ?? (_ => { }))));

    [Fact]
    public async Task Urlsrc_is_reoriginated_onto_the_public_origin_and_keeps_its_trailing_query_marker()
    {
        Uri? fetched = null;
        var d = Build(new()
        {
            ["COLLABORA_URL"] = "https://collabora.internal.example",
            ["COLLABORA_PUBLIC_URL"] = "https://collabora.example.net",
        }, r => fetched = r.RequestUri);

        var url = await d.ActionUrlForDocxAsync(CancellationToken.None);

        // The app asked the server-to-server host...
        Assert.Equal("https://collabora.internal.example/hosting/discovery", fetched?.ToString());
        // ...and hands the browser the public one, path and the bare `?` byte-for-byte intact — callers
        // concatenate `WOPISrc=` straight onto it.
        Assert.Equal("https://collabora.example.net/browser/abc123/cool.html?", url);
    }

    [Fact]
    public async Task Without_a_public_url_the_server_side_origin_is_used_and_the_result_is_cached()
    {
        var calls = 0;
        var d = Build(new() { ["COLLABORA_URL"] = "http://collabora:9980" }, _ => calls++);

        Assert.Equal("http://collabora:9980/browser/abc123/cool.html?", await d.ActionUrlForDocxAsync(CancellationToken.None));
        await d.ActionUrlForDocxAsync(CancellationToken.None);
        Assert.Equal(1, calls); // 24 h cache: one discovery fetch, not one per session mint
    }
}
