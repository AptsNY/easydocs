using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace EasyDocs.Api.Tests;

// ASPNETCORE_FORWARDEDHEADERS_ENABLED=true trusts X-Forwarded-* from ANY client, because the
// framework's setup clears KnownProxies/KnownNetworks. These tests pin the narrowing knob:
// ForwardedHeaders:KnownProxies / :KnownNetworks bind from configuration, and misconfiguration
// aborts boot instead of being silently ignored — silent ignoring is the defect that created
// this feature (issue #17).
public class ForwardedHeadersTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public void Known_proxies_and_networks_bind_from_configuration()
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders_Enabled"] = "true", // what ASPNETCORE_FORWARDEDHEADERS_ENABLED becomes
                ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
                ["ForwardedHeaders:KnownNetworks:0"] = "192.168.0.0/16",
            })));
        using var _ = host.CreateClient(); // boots the host

        var o = host.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Contains(IPAddress.Parse("10.0.0.5"), o.KnownProxies);
        Assert.Contains(IPNetwork.Parse("192.168.0.0/16"), o.KnownIPNetworks);
    }

    // The values only take effect when the middleware is on. Configuring them with the middleware
    // off used to be silently ignored — now it is a boot error that names the missing switch.
    [Fact]
    public void Boot_fails_fast_when_proxies_are_configured_but_the_middleware_is_off()
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5",
            })));

        var ex = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0")] // missing the /prefix length
    public void Boot_fails_fast_on_an_unparseable_proxy_or_network(string key, string value)
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders_Enabled"] = "true",
                [key] = value,
            })));

        var ex = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains(value, ex.Message, StringComparison.Ordinal);
    }

    // The deployment shape behind a Google load balancer (Cloud Run, 2026-09-21): Kestrel's peer is a
    // proxy inside KnownNetworks and X-Forwarded-Proto is what makes the app know it is https --
    // OIDC redirect URIs and every absolute URL the app mints hang off Request.Scheme. The observable
    // is the OpenAPI document's `servers` entry, which ASP.NET builds from the request's scheme and
    // host: exactly what the production smoke script (.github/scripts/smoke-prod.sh) asserts through
    // the real load balancer. (The framework's filter forwards For and Proto only, so Host is not part
    // of this; the load balancer passes the original Host header through untouched.)
    //
    // TestServer reports no peer address, and the middleware deliberately trusts a null peer for the
    // first hop -- so the peer is set explicitly by a startup filter inserted FIRST in the service
    // collection: the first-registered filter is the outermost, so it runs before the framework's own
    // ForwardedHeaders filter.
    [Theory]
    [InlineData("169.254.8.1", "https://localhost/")]   // inside 169.254.0.0/16: trusted, X-Forwarded-Proto applied
    [InlineData("203.0.113.9", "http://localhost/")]    // outside every known network: header ignored
    public async Task Forwarded_proto_is_honoured_only_from_a_known_network(string peer, string expectedServer)
    {
        using var host = f.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders_Enabled"] = "true",
                ["ForwardedHeaders:KnownNetworks:0"] = "169.254.0.0/16", // Cloud Run's local proxy range
            }));
            b.ConfigureServices(s => s.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new FakePeer(IPAddress.Parse(peer)))));
        });
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var doc = await client.GetFromJsonAsync<OpenApiServers>("/openapi/v1.json");

        Assert.Equal(expectedServer, Assert.Single(doc!.Servers).Url);
    }

    private sealed record OpenApiServers(List<Server> Servers);
    private sealed record Server(string Url);

    private sealed class FakePeer(IPAddress ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nxt) => { ctx.Connection.RemoteIpAddress = ip; return nxt(ctx); });
            next(app);
        };
    }
}
