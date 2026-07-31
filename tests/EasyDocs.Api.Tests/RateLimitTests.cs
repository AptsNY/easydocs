using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EasyDocs.Api.Tests;

// Proves the spec §11 rate limits actually reject. A policy nothing tests is a policy that gets
// silently misconfigured, so each policy gets a host with its limit dialled down to 1-2 and an
// assertion on the 429 + problem+json body — plus one test at the SHIPPED defaults proving the
// Playwright suite's registration burst still fits, which is the constraint that sizes them.
public class RateLimitTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    // A second host on the same Postgres container, with the limits tightened. Overrides are appended
    // after ApiFactory's own in-memory source, so they win.
    private WebApplicationFactory<Program> Tightened(params (string Key, string Value)[] overrides) =>
        f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(overrides.ToDictionary(o => o.Key, o => (string?)o.Value))));

    private static async Task AssertProblemJson429(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadFromJsonAsync<ProblemBody>();
        Assert.Equal(429, body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Title));
        Assert.False(string.IsNullOrWhiteSpace(body.Detail));
    }

    private record ProblemBody(string? Title, string? Detail, int Status);
    private record ShareDto(string Token, string Url);

    [Fact]
    public async Task Anonymous_share_view_is_rejected_with_problem_json_past_the_limit()
    {
        // The link is created through the fixture host; the tightened host shares its database.
        var owner = await f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Shared");
        var (versionId, _) = await owner.Client.UploadAsync(docId);
        var created = await owner.Client.PostAsJsonAsync($"/api/v1/versions/{versionId}/share-links", new { expiresAt = (DateTimeOffset?)null });
        created.EnsureSuccessStatusCode();
        var token = (await created.Content.ReadFromJsonAsync<ShareDto>())!.Token;

        using var host = Tightened(
            ("RateLimit:AnonShare:PermitLimit", "2"),
            ("RateLimit:AnonDownload:PermitLimit", "1"));
        var anon = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/s/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/s/{token}")).StatusCode);
        await AssertProblemJson429(await anon.GetAsync($"/s/{token}"));

        // The download has its own bucket — it is the egress cap — so the exhausted view allowance
        // above does not spend it, and it rejects on its own budget.
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync($"/s/{token}/download")).StatusCode);
        await AssertProblemJson429(await anon.GetAsync($"/s/{token}/download"));
    }

    private static object NewRegistration() => new
    {
        email = $"rl-{Guid.NewGuid():N}@example.com",
        displayName = "U",
        password = "pw-at-least-12",
        orgName = $"Org-{Guid.NewGuid():N}",
    };

    // TestAuth.RegisterAsync only extends ApiFactory; these tests need an account on a *tightened* host.
    private static async Task<HttpClient> RegisterOnAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/register", NewRegistration());
        res.EnsureSuccessStatusCode();
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cookie["ed_session=".Length..].Split(';')[0]);
        return client;
    }

    [Fact]
    public async Task Register_rejects_past_its_budget_reports_retry_after_and_does_not_starve_login()
    {
        using var host = Tightened(
            ("RateLimit:Auth:BurstLimit", "1"),
            ("RateLimit:Auth:TokensPerPeriod", "1"),
            ("RateLimit:Auth:ReplenishmentSeconds", "3600"));
        var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", NewRegistration())).StatusCode);

        var rejected = await client.PostAsJsonAsync("/api/v1/auth/register", NewRegistration());
        await AssertProblemJson429(rejected);
        Assert.Equal("3600", rejected.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture));

        // Registration is exhausted, but login keys on its own path: a register flood must not be able
        // to lock every legitimate sign-in out of the install (which is what one shared bucket did, and
        // is the failure mode once a reverse proxy collapses every caller onto a single address).
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "nobody@example.com", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Token_minting_is_partitioned_by_user_not_by_address()
    {
        using var host = Tightened(("RateLimit:TokenMint:PermitLimit", "1"));
        // Both accounts live on the same host and, under TestServer, present the same (null) address —
        // so a second account still minting proves the partition key is the user id, not the client IP.
        var a = await RegisterOnAsync(host);
        var b = await RegisterOnAsync(host);

        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/v1/tokens", new { name = "one", scopes = Array.Empty<string>() })).StatusCode);
        await AssertProblemJson429(await a.PostAsJsonAsync("/api/v1/tokens", new { name = "two", scopes = Array.Empty<string>() }));
        Assert.Equal(HttpStatusCode.Created, (await b.PostAsJsonAsync("/api/v1/tokens", new { name = "one", scopes = Array.Empty<string>() })).StatusCode);
    }

    // The constraint that sizes the shipped auth defaults. The Playwright suite registers ~70 orgs in
    // ~11 seconds from one address, and contributors and CI both re-run it back to back against a
    // long-lived server — so the bucket has to cover SEVERAL runs, not one. That is not hypothetical: a
    // first attempt at 300 tokens passed one run and 429'd nineteen specs on the second. 400 here is
    // ~5 runs' worth of registrations, asserted in Testcontainers where it fails in seconds instead of
    // being discovered in the e2e job. Deliberately invalid bodies — the limiter runs ahead of the
    // handler, so each still spends a token while costing no Argon2id hash and writing no row.
    [Fact]
    public async Task Shipped_auth_defaults_absorb_several_back_to_back_e2e_runs()
    {
        var client = f.CreateClient(); // no overrides: the real defaults
        for (var i = 0; i < 400; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "", displayName = "", password = "", orgName = "" });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    // Placement check for Program.cs: the limiter is opt-in per endpoint, so the static-file fallback
    // that serves the SPA shell is never metered however hard the anonymous share policy is squeezed.
    [Fact]
    public async Task The_spa_fallback_is_never_rate_limited()
    {
        using var host = Tightened(("RateLimit:AnonShare:PermitLimit", "1"));
        var client = host.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/")).StatusCode);
    }
}
