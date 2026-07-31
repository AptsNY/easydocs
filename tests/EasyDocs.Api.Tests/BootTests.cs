using Microsoft.Extensions.Configuration;

namespace EasyDocs.Api.Tests;

public class BootTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_ok_and_migrations_applied()
        => (await f.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();

    // Program.cs calls RequireJwtKeyBytes at boot so a weak signing key aborts startup rather than
    // surfacing at someone's first login. That guard was unasserted: without this test the length check
    // could be deleted and the suite would stay green, because every test host supplies a valid secret.
    [Theory]
    [InlineData("")]                    // missing entirely
    [InlineData("too-short")]           // < 256 bits, so HS256 cannot use it
    [InlineData("31-bytes-xxxxxxxxxxxxxxxxxxxxxx")]
    public void Boot_fails_fast_on_a_missing_or_undersized_jwt_secret(string secret)
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = secret })));

        var ex = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("at least 32 bytes", ex.Message, StringComparison.Ordinal);
    }
}
