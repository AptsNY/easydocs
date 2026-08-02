using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Amazon.S3;
using EasyDocs.Api.Storage;
using EasyDocs.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Testcontainers.Minio;

namespace EasyDocs.Api.Tests;

// Issue #14: the S3-compatible backend, tested against real S3 semantics via MinIO — the same
// Testcontainers approach the rest of the suite uses for Postgres.
public sealed class MinioFixture : IAsyncLifetime
{
    // Pinned like postgres:16 in ApiFactory; this is the tag the builder itself documents.
    public MinioContainer Container { get; } =
        new MinioBuilder("minio/minio:RELEASE.2023-01-31T02-24-19Z").Build();
    public const string Bucket = "easydocs-test";

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        using var s3 = CreateClient();
        await s3.PutBucketAsync(Bucket);
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();

    public AmazonS3Client CreateClient() => new(
        Container.GetAccessKey(), Container.GetSecretKey(),
        new AmazonS3Config
        {
            ServiceURL = Container.GetConnectionString(),
            ForcePathStyle = true,
            // Same settings as S3BlobStore.FromConfiguration: the SDK's default trailing checksums
            // are rejected by this MinIO release.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public Dictionary<string, string?> ApiConfig => new()
    {
        ["BlobStore"] = "s3",
        ["S3:ServiceUrl"] = Container.GetConnectionString(),
        ["S3:AccessKey"] = Container.GetAccessKey(),
        ["S3:SecretKey"] = Container.GetSecretKey(),
        ["S3:Bucket"] = Bucket,
    };
}

public class S3BlobStoreTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private S3BlobStore Store() => new(minio.CreateClient(), MinioFixture.Bucket);

    [Fact]
    public async Task Put_returns_sha_is_write_once_and_roundtrips()
    {
        var store = Store();
        var bytes = Encoding.UTF8.GetBytes("hello s3 docx");

        var r1 = await store.PutAsync(new MemoryStream(bytes));
        var r2 = await store.PutAsync(new MemoryStream(bytes)); // identical content

        Assert.Equal(r1.Sha256, r2.Sha256);
        Assert.Equal(64, r1.Sha256.Length);
        Assert.Equal(bytes.Length, r1.SizeBytes);
        Assert.True(await store.ExistsAsync(r1.Sha256));
        using var read = await store.OpenReadAsync(r1.Sha256);
        using var m = new MemoryStream();
        await read.CopyToAsync(m);
        Assert.Equal(bytes, m.ToArray());
    }

    [Fact]
    public async Task Missing_blob_behaves_exactly_like_the_filesystem_store()
    {
        var store = Store();
        var absent = new string('0', 64);
        Assert.False(await store.ExistsAsync(absent));
        // Same exception type as FileSystemBlobStore — callers must not be able to tell backends apart.
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.OpenReadAsync(absent));
    }
}

// The wiring: a host booted with BlobStore=s3 serves the whole upload/download lifecycle out of the
// bucket, and an unknown BlobStore value refuses to boot.
public class S3ApiTests(ApiFactory f, MinioFixture minio) : IClassFixture<ApiFactory>, IClassFixture<MinioFixture>
{
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Fact]
    public async Task Upload_and_download_roundtrip_through_a_bucket()
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(minio.ApiConfig)));
        var client = host.CreateClient();

        var email = $"s3-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, displayName = "S3", password = "pw-at-least-12", orgName = $"Org-{Guid.NewGuid():N}" });
        reg.EnsureSuccessStatusCode();
        var jwt = reg.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("ed_session="))
            ["ed_session=".Length..].Split(';')[0];
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var docId = (await (await client.PostAsJsonAsync("/api/v1/documents", new { name = "S3 doc" }))
            .Content.ReadFromJsonAsync<DocDto>())!.Id;
        var bytes = DocxFixtures.Base();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(DocxMime);
        var up = await client.PostAsync($"/api/v1/documents/{docId}/versions",
            new MultipartFormDataContent { { part, "file", "d.docx" } });
        Assert.True(up.IsSuccessStatusCode,
            $"upload: {(int)up.StatusCode} {await up.Content.ReadAsStringAsync()}");
        var versionId = (await up.Content.ReadFromJsonAsync<UploadDto>())!.VersionId;

        var download = await client.GetAsync($"/api/v1/versions/{versionId}/download?format=docx");
        download.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void An_unknown_BlobStore_value_refuses_to_boot()
    {
        using var host = f.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["BlobStore"] = "raid0" })));
        var ex = Assert.Throws<InvalidOperationException>(() => host.CreateClient());
        Assert.Contains("raid0", ex.Message, StringComparison.Ordinal);
    }

    private record DocDto(Guid Id);
    private record UploadDto(Guid VersionId);
}
