using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EasyDocs.Api.Auth;

namespace EasyDocs.Api.Tests;

// Issue #10 end to end: setup → enable → login demands a code → the challenge token is useless as
// a session → a live code (or one recovery code, once) finishes the login → disable needs a code.
public class MfaFlowTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    private record RegisterDto(Guid Id, string Email);
    private record SetupDto(string Secret, string OtpauthUri);
    private record EnableDto(string[] RecoveryCodes);
    private record LoginMfaDto(bool MfaRequired, string MfaToken);
    private record StatusDto(bool Enabled, int RecoveryCodesLeft);

    private async Task<(HttpClient Client, string Email, string Password)> RegisterAsync()
    {
        var client = f.CreateClient();
        var email = $"mfa-{Guid.NewGuid():N}@example.com";
        const string password = "pw-at-least-12";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "M", password, orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="))
            ["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        return (client, email, password);
    }

    private static async Task<(string Secret, string[] Recovery)> EnableMfaAsync(HttpClient client)
    {
        var setup = await (await client.PostAsync("/api/v1/account/mfa/setup", null))
            .Content.ReadFromJsonAsync<SetupDto>();
        var enable = await client.PostAsJsonAsync("/api/v1/account/mfa/enable",
            new { code = Totp.Code(setup!.Secret, DateTimeOffset.UtcNow) });
        enable.EnsureSuccessStatusCode();
        var codes = (await enable.Content.ReadFromJsonAsync<EnableDto>())!.RecoveryCodes;
        return (setup.Secret, codes);
    }

    [Fact]
    public async Task The_full_totp_login_flow()
    {
        var (client, email, password) = await RegisterAsync();
        var (secret, _) = await EnableMfaAsync(client);

        var status = await client.GetFromJsonAsync<StatusDto>("/api/v1/account/mfa");
        Assert.True(status!.Enabled);
        Assert.Equal(10, status.RecoveryCodesLeft);

        // Fresh login now stops at the challenge: no cookie, a token instead.
        var fresh = f.CreateClient();
        var login = await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        Assert.DoesNotContain("Set-Cookie", login.Headers.Select(h => h.Key));
        var challenge = await login.Content.ReadFromJsonAsync<LoginMfaDto>();
        Assert.True(challenge!.MfaRequired);

        // The challenge token is not a session.
        var probe = f.CreateClient();
        probe.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", challenge.MfaToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await probe.GetAsync("/api/v1/me")).StatusCode);

        // Wrong code: uniform 401. Right code: cookie.
        var bad = await fresh.PostAsJsonAsync("/api/v1/auth/login/mfa",
            new { mfaToken = challenge.MfaToken, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var good = await fresh.PostAsJsonAsync("/api/v1/auth/login/mfa",
            new { mfaToken = challenge.MfaToken, code = Totp.Code(secret, DateTimeOffset.UtcNow) });
        good.EnsureSuccessStatusCode();
        Assert.Contains(good.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ed_session="));
    }

    [Fact]
    public async Task A_recovery_code_works_exactly_once()
    {
        var (client, email, password) = await RegisterAsync();
        var (_, recovery) = await EnableMfaAsync(client);

        async Task<HttpResponseMessage> LoginWithAsync(string code)
        {
            var fresh = f.CreateClient();
            var login = await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
            var challenge = await login.Content.ReadFromJsonAsync<LoginMfaDto>();
            return await fresh.PostAsJsonAsync("/api/v1/auth/login/mfa",
                new { mfaToken = challenge!.MfaToken, code });
        }

        (await LoginWithAsync(recovery[0])).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginWithAsync(recovery[0])).StatusCode);

        var status = await client.GetFromJsonAsync<StatusDto>("/api/v1/account/mfa");
        Assert.Equal(9, status!.RecoveryCodesLeft);
    }

    [Fact]
    public async Task Disable_requires_a_code_and_restores_plain_login()
    {
        var (client, email, password) = await RegisterAsync();
        var (secret, _) = await EnableMfaAsync(client);

        var noCode = await client.PostAsJsonAsync("/api/v1/account/mfa/disable", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);

        (await client.PostAsJsonAsync("/api/v1/account/mfa/disable",
            new { code = Totp.Code(secret, DateTimeOffset.UtcNow) })).EnsureSuccessStatusCode();

        var fresh = f.CreateClient();
        var login = await fresh.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ed_session="));
    }
}
