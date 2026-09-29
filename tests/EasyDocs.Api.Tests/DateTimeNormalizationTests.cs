using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EasyDocs.Api.Tests;

// Npgsql's `timestamptz` mapping only accepts a UTC (offset 0) DateTimeOffset - any other offset
// throws ArgumentException inside SaveChangesAsync (Microsoft.EntityFrameworkCore.DbUpdateException
// at the HTTP layer), a bare 500. But +02:00/-05:00/Z are all perfectly valid RFC 3339 offsets, and
// the OpenAPI document advertises these fields as `date-time` - so a conforming client in any
// non-UTC timezone got a 500 on a *correct* request, on every one of the three body fields that
// take a DateTimeOffset today: ApprovalEndpoints.RequestBody.DueAt, ShareEndpoints.CreateShareLinkRequest.
// ExpiresAt, TokenEndpoints.CreateTokenRequest.ExpiresAt (grepped for `record.*DateTimeOffset` under
// src/EasyDocs.Api to confirm that's the full list). Fixed once via a JsonConverter<DateTimeOffset>
// registered in ConfigureHttpJsonOptions (minimal-API body binding does not share MVC's JsonOptions),
// which normalizes every inbound value to its UTC instant before it reaches a handler or the DB -
// covering this list and any future DateTimeOffset field without a per-endpoint patch.
public class DateTimeNormalizationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public DateTimeNormalizationTests(ApiFactory f) => _f = f;

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient c, string route, string rawJson) =>
        await c.PostAsync(route, new StringContent(rawJson, Encoding.UTF8, "application/json"));

    private async Task<(Account Owner, Account Approver, Guid VersionId)> SeedPublishedVersionWithApproverAsync()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var approver = await _f.SeedOrgUserAsync(owner.OrgId);
        (await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email = approver.Email, role = "Editor" })).EnsureSuccessStatusCode();
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();
        return (owner, approver, vid);
    }

    [Theory]
    [InlineData("+02:00")]
    [InlineData("-05:00")]
    public async Task Approval_dueAt_with_non_utc_offset_is_201_and_roundtrips_the_same_instant(string offset)
    {
        var (owner, approver, vid) = await SeedPublishedVersionWithApproverAsync();
        var expected = DateTimeOffset.Parse($"2026-08-05T12:00:00{offset}");

        var res = await PostRawAsync(owner.Client, $"/api/v1/versions/{vid}/approvals",
            $"{{\"approverIds\":[\"{approver.UserId}\"],\"dueAt\":\"2026-08-05T12:00:00{offset}\"}}");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        // Read back via the approver's own inbox (spec's actual read path), not just the DB, so
        // this also proves the value the API *serves* is correct, not merely what it stores.
        var list = await approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals?filter=assigned");
        var dueAt = list.GetProperty("items")[0].GetProperty("dueAt").GetDateTimeOffset();
        Assert.Equal(expected, dueAt); // same instant, regardless of the offset it arrived with
    }

    [Fact]
    public async Task Approval_dueAt_with_no_offset_is_treated_as_that_date_at_midnight_utc()
    {
        // A bare "2026-08-05" is a due *date*, not an instant - deliberately accepted (not
        // rejected with 400) and pinned to midnight UTC. This must NOT depend on the parsing
        // machine's local timezone: System.Text.Json's own default does exactly that (assumes the
        // *local* offset), which would make the same request resolve to a different instant on a
        // server in a different timezone - wrong for a public API driven from anywhere.
        var (owner, approver, vid) = await SeedPublishedVersionWithApproverAsync();

        var res = await PostRawAsync(owner.Client, $"/api/v1/versions/{vid}/approvals",
            $"{{\"approverIds\":[\"{approver.UserId}\"],\"dueAt\":\"2026-08-05\"}}");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var list = await approver.Client.GetFromJsonAsync<JsonElement>("/api/v1/approvals?filter=assigned");
        var dueAt = list.GetProperty("items")[0].GetProperty("dueAt").GetDateTimeOffset();
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero), dueAt);
    }

    [Theory]
    [InlineData("+02:00")]
    [InlineData("-05:00")]
    public async Task ShareLink_expiresAt_with_non_utc_offset_is_201_and_roundtrips_the_same_instant(string offset)
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        var expected = DateTimeOffset.Parse($"2026-08-05T12:00:00{offset}");

        var res = await PostRawAsync(owner.Client, $"/api/v1/versions/{vid}/share-links",
            $"{{\"expiresAt\":\"2026-08-05T12:00:00{offset}\"}}");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        // ShareEndpoints has no list/read route for expiresAt (spec §10) - the DB row is the
        // only place to check the stored instant, same discipline as the existing ShareLinkTests.
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var link = await db.ShareLinks.Where(x => x.VersionId == vid).OrderByDescending(x => x.CreatedAt).FirstAsync();
        Assert.Equal(expected, link.ExpiresAt);
    }

    [Theory]
    [InlineData("+02:00")]
    [InlineData("-05:00")]
    public async Task Token_expiresAt_with_non_utc_offset_is_201_and_roundtrips_the_same_instant(string offset)
    {
        var owner = await _f.RegisterAsync();
        var expected = DateTimeOffset.Parse($"2026-08-05T12:00:00{offset}");

        var res = await PostRawAsync(owner.Client, "/api/v1/tokens",
            $"{{\"name\":\"ci\",\"scopes\":[],\"expiresAt\":\"2026-08-05T12:00:00{offset}\"}}");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var list = await owner.Client.GetFromJsonAsync<JsonElement[]>("/api/v1/tokens");
        var expiresAt = list![0].GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(expected, expiresAt);
    }
}
