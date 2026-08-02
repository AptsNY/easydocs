using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using EasyDocs.Api.Data;

namespace EasyDocs.Api.Tests;

// A minimal but honest OIDC provider on a real localhost port: discovery, authorize, token, JWKS,
// userinfo. Issues RS256 id_tokens carrying the nonce and PKCE-agnostic codes. Enough protocol for
// the framework's OpenIdConnect handler to complete a real code flow against it.
public sealed class FakeIdp : IAsyncLifetime
{
    private WebApplication _app = null!;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly Dictionary<string, (string Nonce, string Sub, string Email, string Name)> _codes = [];

    public string Issuer { get; private set; } = null!;
    public string Email { get; set; } = $"sso-{Guid.NewGuid():N}@idp.example.com";
    public string Sub { get; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Sso User";
    public bool EmailVerified { get; set; } = true;

    public async Task InitializeAsync()
    {
        var key = new RsaSecurityKey(_rsa) { KeyId = "fake-idp-key" };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        _app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = Issuer,
            authorization_endpoint = $"{Issuer}/authorize",
            token_endpoint = $"{Issuer}/token",
            jwks_uri = $"{Issuer}/jwks",
            userinfo_endpoint = $"{Issuer}/userinfo",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));

        _app.MapGet("/jwks", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "fake-idp-key" });
            return Results.Json(new { keys = new[] { new { jwk.Kty, jwk.N, jwk.E, jwk.Kid, Use = "sig" } } });
        });

        _app.MapGet("/authorize", (HttpRequest req) =>
        {
            var code = Guid.NewGuid().ToString("N");
            _codes[code] = (req.Query["nonce"]!, Sub, Email, Name);
            var redirect = $"{req.Query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(req.Query["state"]!)}";
            return Results.Redirect(redirect);
        });

        _app.MapPost("/token", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var (nonce, sub, email, name) = _codes[form["code"]!];
            var handler = new JwtSecurityTokenHandler();
            var idToken = handler.WriteToken(handler.CreateJwtSecurityToken(
                issuer: Issuer,
                audience: form["client_id"].ToString() is { Length: > 0 } aud ? aud : "easydocs-client",
                subject: new ClaimsIdentity(new[]
                {
                    new Claim("sub", sub), new Claim("nonce", nonce), new Claim("email", email),
                    new Claim("email_verified", EmailVerified ? "true" : "false"), new Claim("name", name),
                }),
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(5),
                issuedAt: DateTime.UtcNow,
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
            return Results.Json(new
            {
                access_token = $"at-{sub}",
                token_type = "Bearer",
                expires_in = 300,
                id_token = idToken,
            });
        });

        _app.MapGet("/userinfo", () => Results.Json(new
        {
            sub = Sub,
            email = Email,
            email_verified = EmailVerified,
            name = Name,
        }));

        await _app.StartAsync();
        Issuer = _app.Urls.First();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }
}

// Issue #9 end to end: challenge → fake IdP → callback → handshake → ed_session, with the user
// provisioned on first sign-in (own org, null password hash) and reused on the second.
public class OidcFlowTests(ApiFactory f, FakeIdp idp) : IClassFixture<ApiFactory>, IClassFixture<FakeIdp>
{
    private (HttpClient Api, HttpClient Raw) Clients()
    {
        var factory = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Authority"] = idp.Issuer,
                ["Oidc:ClientId"] = "easydocs-client",
                ["Oidc:ClientSecret"] = "easydocs-secret",
            })));
        var api = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            // https base: the handshake/correlation cookies are Secure, and a cookie jar refuses to
            // replay Secure cookies over plain http.
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        return (api, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }));
    }

    private async Task<HttpClient> SignInWithSsoAsync()
    {
        var (api, raw) = Clients();

        var challenge = await api.GetAsync("/api/v1/auth/oidc/login");
        Assert.Equal(HttpStatusCode.Found, challenge.StatusCode);
        var authorizeUrl = challenge.Headers.Location!.ToString();
        Assert.StartsWith(idp.Issuer, authorizeUrl);

        var fromIdp = await raw.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, fromIdp.StatusCode);
        var callback = fromIdp.Headers.Location!.ToString();
        Assert.Contains("/api/v1/auth/oidc/callback", callback);

        var callbackRes = await api.GetAsync(callback);
        Assert.True(callbackRes.StatusCode == HttpStatusCode.Found,
            $"callback: {(int)callbackRes.StatusCode} {(await callbackRes.Content.ReadAsStringAsync())[..Math.Min(600, (await callbackRes.Content.ReadAsStringAsync()).Length)]}");
        Assert.Contains("/api/v1/auth/oidc/complete", callbackRes.Headers.Location!.ToString());

        var complete = await api.GetAsync("/api/v1/auth/oidc/complete");
        Assert.Equal(HttpStatusCode.Found, complete.StatusCode);
        Assert.Equal("/", complete.Headers.Location!.ToString());
        return api;
    }

    [Fact]
    public async Task Sso_signs_in_provisions_once_and_reuses_the_account()
    {
        var api = await SignInWithSsoAsync();

        var me = await api.GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal(idp.Email, me!.Email);
        Assert.Equal(idp.Name, me.DisplayName);

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            var user = await db.Users.SingleAsync(u => u.Email == idp.Email);
            Assert.Null(user.PasswordHash); // SSO-only account — local login's uniform 401 covers it
        }

        // Second sign-in: same user, no second provisioning.
        var again = await SignInWithSsoAsync();
        var meAgain = await again.GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal(me.Id, meAgain!.Id);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EasyDocsDbContext>();
            Assert.Equal(1, await db.Users.CountAsync(u => u.Email == idp.Email));
        }
    }

    [Fact]
    public async Task Login_endpoint_404s_when_sso_is_not_configured()
    {
        var api = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/v1/auth/oidc/login")).StatusCode);
        var enabled = await api.GetFromJsonAsync<EnabledDto>("/api/v1/auth/oidc");
        Assert.False(enabled!.Enabled);
    }

    private record MeDto(Guid Id, string Email, string DisplayName);
    private record EnabledDto(bool Enabled);
}
