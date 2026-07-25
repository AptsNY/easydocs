namespace EasyDocs.Api.Tests;

public class BootTests(ApiFactory f) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_endpoint_returns_ok_and_migrations_applied()
        => (await f.CreateClient().GetAsync("/health")).EnsureSuccessStatusCode();
}
