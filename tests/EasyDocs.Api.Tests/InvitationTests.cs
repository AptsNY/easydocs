using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Data;
using EasyDocs.Api.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// POST /api/v1/invitations/{token}:accept (spec §10.1 Auth) — the only path by which someone without
// an account, or a user in another org, joins a document.
public class InvitationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _f;
    public InvitationTests(ApiFactory f) => _f = f;

    private record InviteDto(string Email, string Role, string InvitationToken);
    private record AcceptDto(Guid OrgId, Guid? DocumentId, string? DocRole);
    private record MemberDto(Guid UserId, string Email, string DisplayName, string Role);

    private async Task<(Account Owner, Guid DocId, string Email, string Token)> InviteAsync(string role = "Editor")
    {
        var owner = await _f.RegisterAsync();
        var docId = await owner.Client.CreateDocAsync();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";

        var res = await owner.Client.PostAsJsonAsync($"/api/v1/documents/{docId}/members", new { email, role });
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<InviteDto>())!;
        return (owner, docId, email, dto.InvitationToken);
    }

    // Accepting rebinds the session to the invited org, so the client must pick up the new cookie.
    private static void AdoptSession(HttpClient client, HttpResponseMessage res)
    {
        var cookie = res.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session=", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", cookie["ed_session=".Length..].Split(';')[0]);
    }

    [Fact]
    public async Task Invited_user_accepts_and_gains_document_access()
    {
        var (owner, docId, email, token) = await InviteAsync("Editor");

        // The invitee registers (which creates their own org) and cannot see the document yet.
        var invitee = await _f.RegisterAsync(email);
        Assert.Equal(HttpStatusCode.NotFound, (await invitee.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);

        var accept = await invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var dto = (await accept.Content.ReadFromJsonAsync<AcceptDto>())!;
        Assert.Equal(owner.OrgId, dto.OrgId);
        Assert.Equal(docId, dto.DocumentId);
        Assert.Equal("Editor", dto.DocRole);

        AdoptSession(invitee.Client, accept);
        Assert.Equal(HttpStatusCode.OK, (await invitee.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await invitee.Client.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm())).StatusCode);

        // And the owner now sees them on the roster with the invited role.
        var members = await (await owner.Client.GetAsync($"/api/v1/documents/{docId}/members"))
            .Content.ReadFromJsonAsync<MemberDto[]>();
        Assert.Equal("Editor", members!.Single(m => m.Email == email).Role);
    }

    [Fact]
    public async Task Invited_viewer_can_read_but_not_write()
    {
        var (_, docId, email, token) = await InviteAsync("Viewer");
        var invitee = await _f.RegisterAsync(email);

        var accept = await invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        accept.EnsureSuccessStatusCode();
        AdoptSession(invitee.Client, accept);

        Assert.Equal(HttpStatusCode.OK, (await invitee.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await invitee.Client.PostAsync($"/api/v1/documents/{docId}/versions", TestAuth.DocxForm())).StatusCode);
    }

    [Fact]
    public async Task Reusing_a_token_is_409()
    {
        var (_, _, email, token) = await InviteAsync();
        var invitee = await _f.RegisterAsync(email);

        var first = await invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_different_user_cannot_accept_someone_elses_invitation()
    {
        var (_, docId, _, token) = await InviteAsync();
        var stranger = await _f.RegisterAsync(); // a different email

        var res = await stranger.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/v1/documents/{docId}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_token_is_404()
    {
        var a = await _f.RegisterAsync();
        var res = await a.Client.PostAsync($"/api/v1/invitations/not-a-real-token:accept", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Expired_invitation_is_404()
    {
        var (_, _, email, token) = await InviteAsync();

        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var invite = await db.Invitations.SingleAsync(i => i.Email == email);
            invite.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var invitee = await _f.RegisterAsync(email);
        var res = await invitee.Client.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Anonymous_accept_is_401()
    {
        var (_, _, _, token) = await InviteAsync();
        var anon = _f.CreateClient();
        var res = await anon.PostAsync($"/api/v1/invitations/{token}:accept", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Raw_token_is_not_stored()
    {
        var (_, _, email, token) = await InviteAsync();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
        var invite = await db.Invitations.SingleAsync(i => i.Email == email);
        Assert.NotEqual(token, invite.TokenHash);
        Assert.DoesNotContain(token, invite.TokenHash);
    }
}
