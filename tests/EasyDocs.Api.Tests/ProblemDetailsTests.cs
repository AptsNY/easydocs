using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EasyDocs.Api.Tests.Fixtures;

namespace EasyDocs.Api.Tests;

// A malformed JSON body (bad GUID, truncated JSON, an unparseable date, a missing body) fails
// model binding *before* any handler runs, throwing Microsoft.AspNetCore.Http.BadHttpRequestException
// (StatusCode 400) which wraps a System.Text.Json.JsonException. Program.cs registered no
// exception-handling middleware at all, so that exception propagated unhandled -> a bare 500 in
// production (a client is told to retry a request that can never succeed, and malformed input
// from the internet pages an operator). Verified empirically (see the diagnostic run in the PR
// description) rather than assumed: BadHttpRequestException is exactly, and only, what surfaces
// for a body-binding failure.
public class ProblemDetailsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public ProblemDetailsTests(ApiFactory f) => _f = f;

    private static async Task<HttpResponseMessage> PostRawAsync(HttpClient c, string route, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await c.PostAsync(route, content);
    }

    private static async Task AssertProblemJson400Async(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        // Actionable, not a leaked stack trace: title/detail must be short, human strings.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.DoesNotContain("StackTrace", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_datetimeoffset_in_approvals_body_is_400_not_500()
    {
        // "2026-08-05" (the literal repro in the bug report) is NOT actually a JSON-binding
        // failure: System.Text.Json parses a date with no offset by assuming the *local
        // machine's* offset (documented ISO-8601 behavior), so it binds fine and only blew up
        // later inside the handler when Npgsql refused a non-UTC offset for `timestamptz`
        // (DbUpdateException, not BadHttpRequestException). That was a distinct, separate bug,
        // since fixed - see DateTimeNormalizationTests, which normalizes every inbound
        // DateTimeOffset to UTC before it ever reaches a handler. A value that truly cannot parse
        // as a DateTimeOffset at all is what actually reaches the JSON-binding chokepoint this
        // test covers.
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync("Doc");
        var (vid, _) = await owner.Client.UploadAsync(docId, DocxFixtures.Base());
        (await owner.Client.PostAsJsonAsync($"/api/v1/versions/{vid}/publish", new { kind = "minor" }))
            .EnsureSuccessStatusCode();

        var res = await PostRawAsync(owner.Client, $"/api/v1/versions/{vid}/approvals",
            $"{{\"approverIds\":[\"{owner.UserId}\"],\"dueAt\":\"not-a-date\"}}");

        await AssertProblemJson400Async(res);
    }

    [Fact]
    public async Task Truncated_json_on_create_document_is_400_not_500()
    {
        var owner = await _f.RegisterAsync();
        var res = await PostRawAsync(owner.Client, "/api/v1/documents", "{\"name\":");
        await AssertProblemJson400Async(res);
    }

    [Fact]
    public async Task Malformed_guid_in_body_is_400_not_500()
    {
        var owner = await _f.RegisterAsync();
        var res = await PostRawAsync(owner.Client, "/api/v1/documents",
            "{\"name\":\"x\",\"folderId\":\"not-a-guid\"}");
        await AssertProblemJson400Async(res);
    }

    [Fact]
    public async Task Empty_body_where_one_is_required_is_400_not_500()
    {
        var owner = await _f.RegisterAsync();
        var res = await PostRawAsync(owner.Client, "/api/v1/documents", "");
        await AssertProblemJson400Async(res);
    }

    // Proves the handler is narrow: a genuine server-side fault must still be a 500, or the fix
    // would hide real bugs (worse than the bug it fixes). This test originally used a real,
    // naturally-occurring fault reachable from an HTTP request: ApprovalEndpoints.Request used to
    // persist a client-supplied DateTimeOffset without normalizing it to UTC, and Npgsql's
    // `timestamptz` mapping throws ArgumentException for any non-zero offset - a
    // Microsoft.EntityFrameworkCore.DbUpdateException, distinct from BadHttpRequestException. A
    // follow-up fix (see DateTimeNormalizationTests) closed that fault by normalizing every
    // inbound DateTimeOffset to UTC in a JsonConverter, so it can no longer be used as a live
    // repro here. We didn't go looking for a replacement crash to keep this Fact alive - a
    // synthetic one would mean adding test-only throwing code to src/, which the original brief
    // for this fix explicitly ruled out. Narrowness is instead a structural guarantee, readable
    // directly at the call site in Program.cs: the exception-handler branch pattern-matches only
    // `BadHttpRequestException` and `throw error;`s anything else unchanged, so any other
    // exception type (NullReferenceException, a future DbUpdateException, etc.) is never
    // intercepted and necessarily keeps whatever status the framework gives it by default (500).
}
