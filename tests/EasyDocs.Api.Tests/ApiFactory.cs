using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;

namespace EasyDocs.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16").Build();

    public string BlobRoot { get; } = Directory.CreateTempSubdirectory().FullName;

    // ONE Gotenberg for the whole test run: every test class builds its own factory, and a container per
    // factory would be dozens of LibreOffice processes. Started on first use, torn down by Testcontainers'
    // resource reaper when the run ends.
    private static readonly Lazy<Task<string>> Gotenberg = new(async () =>
    {
        // Same digest as deploy/compose (8.37.0), so the tests and the shipped stack cannot drift.
        IContainer c = new ContainerBuilder(
                "gotenberg/gotenberg@sha256:f29984bd1e226bf1b93ba90af06000afa8b315853e99d27b9aaa41b93f15c769")
            .WithPortBinding(3000, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/health")))
            .Build();
        await c.StartAsync();
        return $"http://{c.Hostname}:{c.GetMappedPublicPort(3000)}";
    });

    private string _gotenbergUrl = "";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), Gotenberg.Value);
        _gotenbergUrl = await Gotenberg.Value;
    }

    // For tests that drive deploy/scripts against the suite's own database (docker exec by id).
    public string PostgresContainerId => _pg.Id;

    public new Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder b) =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _pg.GetConnectionString(),
            ["BLOB_ROOT"] = BlobRoot,
            ["Jwt:Secret"] = "test-secret-at-least-32-bytes-long-xxxxx",
            ["PUBLIC_BASE_URL"] = "http://localhost",
            ["WOPI_HOST_URL"]   = "http://localhost",
            ["COLLABORA_URL"]   = "http://localhost:9980",
            ["COLLABORA_ACTION_URL"] = "http://localhost:9980/browser/dist/cool.html?", // test seam: skip live discovery
            ["GOTENBERG_URL"] = _gotenbergUrl,
            ["Jobs:PollSeconds"] = "1", // durable-queue tests plant rows with no nudge; don't wait 15s
        }));
}
