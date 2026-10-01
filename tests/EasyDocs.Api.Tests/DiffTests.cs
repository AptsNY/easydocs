using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Diffing;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public class DiffTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DiffTests(ApiFactory f) => _f = f;

    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private async Task<HttpClient> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"diff-{Guid.NewGuid():N}@example.com";
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
    private record SummaryDto(int Insertions, int Deletions, int Moves, int FormatChanges);
    private record ProblemDto(string Title, string Detail);

    private static async Task<Guid> UploadAsync(HttpClient c, Guid docId, byte[] bytes)
    {
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(bytes));
        up.EnsureSuccessStatusCode();
        return (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
    }

    // version_diffs is keyed by content hash, so a query like "any row with an HTML blob" answers for the
    // WHOLE assembly, not for the pair under test — it passed only because these were the only writers.
    // Scope every assertion to the two versions the test actually created.
    private async Task<(string From, string To)> ShasAsync(Guid v1, Guid v2)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var from = await db.Versions.Where(v => v.Id == v1).Select(v => v.BlobSha256).SingleAsync();
        var to = await db.Versions.Where(v => v.Id == v2).Select(v => v.BlobSha256).SingleAsync();
        return (from, to);
    }

    // Base @ 0.0.1, Edited @ 0.0.2 (import = fast-forward on main -> parent is the base version).
    private async Task<(Guid docId, Guid v1, Guid v2)> BaseAndEditedAsync(HttpClient c)
    {
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Diff" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var up = await c.PostAsync($"/api/v1/documents/{docId}/versions", Docx(DocxFixtures.Base()));
        up.EnsureSuccessStatusCode();
        var v1 = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        var imp = await c.PostAsync($"/api/v1/documents/{docId}/versions:import", Docx(DocxFixtures.Edited()));
        imp.EnsureSuccessStatusCode();
        var v2 = (await imp.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;
        return (docId, v1, v2);
    }

    [Fact]
    public async Task Numeric_summary_computed_eagerly_after_commit()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, v2) = await BaseAndEditedAsync(c);
        var shas = await ShasAsync(v1, v2);

        // Eager BackgroundService populates the numeric summary; poll the row until it lands.
        var populated = false;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            populated = await db.VersionDiffs.AnyAsync(
                d => d.FromSha256 == shas.From && d.ToSha256 == shas.To
                     && (d.Insertions != null || d.Deletions != null));
            if (populated) break;
            await Task.Delay(250);
        }
        Assert.True(populated, "worker did not populate the numeric summary within 15s");

        var summary = await c.GetFromJsonAsync<SummaryDto>(
            $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=summary");
        Assert.True(summary!.Insertions > 0 || summary.Deletions > 0);
    }

    [Fact]
    public async Task Redline_html_on_demand_and_cached()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, v2) = await BaseAndEditedAsync(c);
        var shas = await ShasAsync(v1, v2);

        var url = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=html";
        var first = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/html", first.Content.Headers.ContentType?.MediaType);

        // First compare caches the HTML blob on the VersionDiff row.
        string? htmlSha;
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.VersionDiffs.SingleAsync(
                d => d.FromSha256 == shas.From && d.ToSha256 == shas.To);
            htmlSha = row.HtmlBlobSha256;
            Assert.NotNull(htmlSha);
        }

        var second = await c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Row still points at the same cached blob (not recomputed into a new one).
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var row = await db.VersionDiffs.SingleAsync(
                d => d.FromSha256 == shas.From && d.ToSha256 == shas.To);
            Assert.Equal(htmlSha, row.HtmlBlobSha256);
        }
    }

    // RenderHtml used to walk each paragraph twice — text and insertions, then deletions — so every
    // deletion was exiled to the end of its paragraph: "…in <ins>arrears</ins> …office.<del>monthly</del>".
    // A reader could not tell what replaced what. Deletions must sit where they occur.
    [Fact]
    public async Task Redline_html_keeps_deletions_where_they_occur()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Order" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var marker = Guid.NewGuid().ToString("N"); // own (from,to) pair — no shared cache row
        var v1 = await UploadAsync(c, docId, DocxFixtures.Build($"Tenant shall pay rent monthly in advance to the office {marker}."));
        var v2 = await UploadAsync(c, docId, DocxFixtures.Build($"Tenant shall pay rent quarterly in arrears to the office {marker}."));

        var html = await c.GetStringAsync($"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=html");

        var monthly = html.IndexOf("monthly", StringComparison.Ordinal);
        var advance = html.IndexOf("advance", StringComparison.Ordinal);
        Assert.True(monthly >= 0 && advance >= 0, html);
        Assert.True(monthly < html.IndexOf(" in ", StringComparison.Ordinal), html);
        Assert.True(advance < html.IndexOf("office", StringComparison.Ordinal), html);
    }

    // Redline HTML is cached by content (ADR-7), so HTML rendered by the old two-pass walk would keep
    // being served for every pair compared before the fix. A cached render from an older renderer is
    // recomputed, not served.
    [Fact]
    public async Task Redline_html_cached_by_an_older_renderer_is_recomputed()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Stale" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var (from, to) = DocxFixtures.UniquePair();
        var v1 = await UploadAsync(c, docId, from);
        var v2 = await UploadAsync(c, docId, to);
        var shas = await ShasAsync(v1, v2);
        var pair = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}";
        // A summary compare creates the row, so seeding it below cannot race the eager worker's insert.
        (await c.GetAsync($"{pair}&format=summary")).EnsureSuccessStatusCode();

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
            var stale = await blobs.PutAsync(new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("<article class=\"redline\"><p>STALE</p></article>")));
            var row = await db.VersionDiffs.SingleAsync(d => d.FromSha256 == shas.From && d.ToSha256 == shas.To);
            row.HtmlBlobSha256 = stale.Sha256;
            await db.SaveChangesAsync();
        }

        var html = await c.GetStringAsync($"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=html");
        Assert.DoesNotContain("STALE", html);
        Assert.Contains("EDITED", html);
    }

    // The three compare formats have to agree about WHETHER a comparison exists (spec §7). summary used to
    // swallow DiffSummary.Available == false and answer 0/0/0/0, which is indistinguishable from "these
    // two versions are identical" — the real corpus has both cases (a legacy .doc that cannot be compared,
    // and leases re-saved with no wording change).
    [Fact]
    public async Task Summary_of_an_uncomparable_pair_is_422_like_the_other_formats()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Uncomparable" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;

        // Bytes that are not OOXML at all. The ingest route is content-addressed storage, not a validator,
        // so this is the honest way to reach "cannot compare" — no stubbing.
        var v1 = await UploadAsync(c, docId, System.Text.Encoding.ASCII.GetBytes("not a docx 1"));
        var v2 = await UploadAsync(c, docId, System.Text.Encoding.ASCII.GetBytes("not a docx 2"));

        var pair = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}";
        var summary = await c.GetAsync($"{pair}&format=summary");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, summary.StatusCode);
        Assert.Equal("Comparison unavailable", (await summary.Content.ReadFromJsonAsync<ProblemDto>())!.Title);

        // Unchanged: html still degrades to the graceful 200 message the SPA renders, docx still 422.
        var html = await c.GetAsync($"{pair}&format=html");
        Assert.Equal(HttpStatusCode.OK, html.StatusCode);
        Assert.Equal("<p>Comparison unavailable.</p>", await html.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.GetAsync($"{pair}&format=docx")).StatusCode);
    }

    // ...and a REAL zero stays a 200 with zeros: two versions whose text is identical are compared
    // successfully and have no changes. 0/0 is not the failure signal.
    [Fact]
    public async Task Summary_of_an_unchanged_pair_is_a_200_with_zeros()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, _) = await BaseAndEditedAsync(c);

        var res = await c.GetAsync($"/api/v1/documents/{docId}/compare?from={v1}&to={v1}&format=summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = (await res.Content.ReadFromJsonAsync<SummaryDto>())!;
        Assert.Equal(0, summary.Insertions);
        Assert.Equal(0, summary.Deletions);
    }

    [Fact]
    public async Task WmlComparer_failure_degrades_not_throws()
    {
        using var scope = _f.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IBlobStore>();
        var svc = scope.ServiceProvider.GetRequiredService<WmlComparerDiffService>();

        var stored = await blobs.PutAsync(new MemoryStream(DocxFixtures.Malformed()));

        var result = await svc.SummaryAsync(stored.Sha256, stored.Sha256, default);
        Assert.False(result.Available);
    }

    // The eager DiffSummaryWorker and a user's inline compare routinely target the same pair — uploading
    // a version enqueues the job, and clicking Compare straight afterwards computes it again. version_diffs
    // is keyed by (from_sha, to_sha), so the loser of that insert race used to throw DbUpdateException
    // inside the same try that guards the comparison itself, and the caller was told the documents could
    // not be compared. The compare endpoint turns that into a 422, so two perfectly comparable versions
    // reported "Comparison unavailable" — intermittently, which is the worst way to learn about it.
    //
    // Driven through the API, not the service directly: uploading is what creates the `blobs` rows that
    // version_diffs' foreign keys require, so a service-level test using IBlobStore.PutAsync (which only
    // writes the file) would fail on the FK long before reaching the primary-key race it means to test.
    // A unique fixture pair keeps this row out of every other diff test's way.
    [Fact]
    public async Task Concurrent_compares_of_the_same_pair_never_report_uncomparable()
    {
        var c = await AuthedClientAsync();
        var (fromBytes, toBytes) = DocxFixtures.UniquePair();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Race" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var v1 = await UploadAsync(c, docId, fromBytes);
        var v2 = await UploadAsync(c, docId, toBytes);

        // Six callers, one pair. The eager worker is already computing this same pair off the upload, so
        // the field is larger than six — which is the point.
        var url = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=summary";
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => c.GetAsync(url)));

        // 422 here means "these two versions could not be compared" — the answer a lost cache-row insert
        // used to produce for two documents that compare perfectly well.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        var summaries = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<SummaryDto>()));
        Assert.All(summaries, s => Assert.Equal(summaries[0]!.Insertions, s!.Insertions));
        Assert.All(summaries, s => Assert.Equal(summaries[0]!.Deletions, s!.Deletions));
    }

    // The version page (spec 2026-09-30) draws a version's changes as real pages: the redline docx through
    // Gotenberg. from == to is a version shown on its own (0.0.1 has no parent): its own pages, unmarked,
    // without running the comparer on a document against itself.
    [Fact]
    public async Task Pdf_format_renders_the_redline_and_a_single_version_as_pages()
    {
        var c = await AuthedClientAsync();
        var (docId, v1, v2) = await BaseAndEditedAsync(c);

        // The .docx redline is a named attachment, so a plain link saves "redline.docx", not "compare".
        var docx = await c.GetAsync($"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=docx");
        Assert.Equal(HttpStatusCode.OK, docx.StatusCode);
        Assert.Equal("redline.docx", docx.Content.Headers.ContentDisposition?.FileNameStar ?? docx.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        foreach (var (from, to) in new[] { (v1, v2), (v1, v1) })
        {
            var res = await c.GetAsync($"/api/v1/documents/{docId}/compare?from={from}&to={to}&format=pdf");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal("inline", res.Content.Headers.ContentDisposition?.DispositionType);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        }

        // Showing a version on its own must not leave a compare-with-itself row behind.
        var (sha, _) = await ShasAsync(v1, v1);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocs.Api.Data.EasyDocsDbContext>();
        Assert.False(await db.VersionDiffs.AnyAsync(x => x.FromSha256 == sha && x.ToSha256 == sha));
    }

    [Fact]
    public async Task Pdf_format_of_an_uncomparable_pair_is_422()
    {
        var c = await AuthedClientAsync();
        var docId = (await (await c.PostAsJsonAsync("/api/v1/documents", new { name = "Uncomparable pdf" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var v1 = await UploadAsync(c, docId, System.Text.Encoding.ASCII.GetBytes("not a docx 1"));
        var v2 = await UploadAsync(c, docId, System.Text.Encoding.ASCII.GetBytes("not a docx 2"));

        var res = await c.GetAsync($"/api/v1/documents/{docId}/compare?from={v1}&to={v2}&format=pdf");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    // One Gotenberg serves publish renders too, so format=pdf is metered per user (RateLimits.Render); the
    // route's other formats are not.
    [Fact]
    public async Task Pdf_format_is_rate_limited_per_user_and_other_formats_are_not()
    {
        using var host = _f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimit:Render:PermitLimit"] = "1" })));
        var c = host.CreateClient();
        var email = $"rl-{Guid.NewGuid():N}@example.com";
        var reg = await c.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "R", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(x => x.StartsWith("ed_session="))["ed_session=".Length..].Split(';')[0];
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        var (docId, v1, v2) = await BaseAndEditedAsync(c);
        var pair = $"/api/v1/documents/{docId}/compare?from={v1}&to={v2}";

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{pair}&format=pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync($"{pair}&format=pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{pair}&format=summary")).StatusCode);
    }
}
