using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;

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
}
