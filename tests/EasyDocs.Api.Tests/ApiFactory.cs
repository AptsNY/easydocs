using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace EasyDocs.Api.Tests;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg =
        new PostgreSqlBuilder("postgres:16").Build();

    public string BlobRoot { get; } = Directory.CreateTempSubdirectory().FullName;

    public Task InitializeAsync() => _pg.StartAsync();

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
        }));
}
