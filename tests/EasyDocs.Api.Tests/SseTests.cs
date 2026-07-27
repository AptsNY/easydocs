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

    private async Task CommitVersionAsync(Guid docId, Guid userId, byte[] bytes)
    {
        using var scope = _f.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var stored = await sp.GetRequiredService<IBlobStore>().PutAsync(new MemoryStream(bytes), default);
        await sp.GetRequiredService<VersioningService>().CommitSaveAsync(
            new CommitInput(docId, stored.Sha256, stored.SizeBytes, VersionSource.Upload, userId), default);
    }
}
