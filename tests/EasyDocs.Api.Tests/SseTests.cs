using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Domain;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests;
using EasyDocs.Api.Versioning;
using Microsoft.Extensions.DependencyInjection;

public class SseTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public SseTests(ApiFactory f) => _f = f;

    private async Task<(HttpClient client, Guid userId)> AuthedClientAsync()
    {
        var client = _f.CreateClient();
        var email = $"sse-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "D", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var userId = (await reg.Content.ReadFromJsonAsync<RegisterDto>())!.Id;
        var setCookie = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="));
        var jwt = setCookie["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, userId);
    }

    private static async Task<Guid> CreateDocAsync(HttpClient c)
    {
        var create = await c.PostAsJsonAsync("/api/v1/documents", new { name = "Doc" });
        create.EnsureSuccessStatusCode();
        return (await create.Content.ReadFromJsonAsync<DocDto>())!.Id;
    }

    private record RegisterDto(Guid Id);
    private record DocDto(Guid Id);
    private record InviteDto(string Email, string Role, string InvitationToken);

    [Fact]
    public async Task Version_created_delivered_over_sse()
    {
        var (c, userId) = await AuthedClientAsync();
        var docId = await CreateDocAsync(c);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var resp = await c.GetAsync($"/api/v1/documents/{docId}/events",
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        // Let the endpoint register its subscriber channel before we publish (SSE is subscribe-then-receive).
        await reader.ReadLineAsync(cts.Token); // ": hello" prime line
        await Task.Delay(200, cts.Token);

        // Drive the real write path in-process. (TestServer's in-memory HttpClient can't read a second
        // response while this SSE stream is open, so we can't upload over HTTP concurrently — but this
        // still exercises CommitSaveAsync -> EventBus -> the /events endpoint the client is connected to.)
        await CommitVersionAsync(docId, userId, new byte[] { 1, 2, 3 });

        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
            if (line == "event: version.created") return; // delivered
        Assert.Fail("stream ended before version.created arrived");
    }

    [Fact]
    public async Task Events_rejected_for_non_member()
    {
        var (owner, _) = await AuthedClientAsync();
        var docId = await CreateDocAsync(owner);

        var (other, _) = await AuthedClientAsync(); // different user + org
        var resp = await other.GetAsync($"/api/v1/documents/{docId}/events",
            HttpCompletionOption.ResponseHeadersRead);
        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode); // 404 cross-org (or 403)
    }

    // spec §10.2: member.added is a declared v1 event, but until now MemberEndpoints.Add wrote the
    // audit row and stopped — an open console never learned a direct add changed the roster.
    [Fact]
    public async Task Member_added_delivered_over_sse_for_a_direct_add()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync();
        var addedUser = await _f.SeedOrgUserAsync(owner.OrgId);

        var events = await _f.CaptureEventsAsync(docId,
            () => owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
                new { email = addedUser.Email, role = "Editor" }),
            until: "member.added");

        Assert.Contains("member.added", events);
    }

    // The other path onto a roster: an invitation minted for an unknown/cross-org email only becomes a
    // real DocumentMember row when it is accepted (InvitationEndpoints.Accept), so that is where
    // member.added belongs too — minting the invitation itself changes nothing yet.
    [Fact]
    public async Task Member_added_delivered_over_sse_when_an_invitation_is_accepted()
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";

        var invite = await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members",
            new { email, role = "Viewer" });
        invite.EnsureSuccessStatusCode();
        var token = (await invite.Content.ReadFromJsonAsync<InviteDto>())!.InvitationToken;

        var invitee = await _f.RegisterAsync(email);

        var events = await _f.CaptureEventsAsync(docId,
            () => invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null),
            until: "member.added");

        Assert.Contains("member.added", events);
    }

    private async Task CommitVersionAsync(Guid docId, Guid userId, byte[] bytes)
    {
        using var scope = _f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var stored = await sp.GetRequiredService<IBlobStore>().PutAsync(new MemoryStream(bytes), default);
        await sp.GetRequiredService<VersioningService>().CommitSaveAsync(
            new CommitInput(docId, stored.Sha256, stored.SizeBytes, VersionSource.Upload, userId), default);
    }
}
