using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace EasyDocs.Api.Storage;

// S3-compatible blob backend (issue #14): the env-configurable swap for the filesystem volume.
// Same contract as FileSystemBlobStore — content-addressed by sha256, write-once, one object per
// blob keyed by the bare sha (S3 needs no directory sharding; the key IS the index).
//
// The incoming stream is spooled to a temp file first, because the key is the content hash and the
// hash isn't known until the bytes have all passed — and S3 wants the length up front anyway.
public sealed class S3BlobStore(IAmazonS3 s3, string bucket) : IBlobStore
{
    public static S3BlobStore FromConfiguration(IConfiguration cfg)
    {
        string Require(string key) => cfg[key] ?? throw new InvalidOperationException(
            $"BlobStore=s3 requires {key.Replace(':', '_').Replace("_", "__")} to be configured.");

        var config = new AmazonS3Config
        {
            // ServiceURL covers MinIO / R2 / Ceph; real AWS uses RegionEndpoint from S3__Region.
            ForcePathStyle = cfg.GetValue("S3:ForcePathStyle", true),
            // The SDK's default flexible checksums use aws-chunked trailing checksums, which several
            // S3-compatible stores reject ("x-amz-content-sha256 does not match"). Integrity is
            // already ours: the key IS the sha256 of the bytes.
            RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (cfg["S3:ServiceUrl"] is { Length: > 0 } url) config.ServiceURL = url;
        else config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(Require("S3:Region"));

        return new S3BlobStore(
            new AmazonS3Client(Require("S3:AccessKey"), Require("S3:SecretKey"), config),
            Require("S3:Bucket"));
    }

    public async Task<BlobResult> PutAsync(Stream content, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"easydocs-s3-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            string sha;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            // Write-once: identical content already stored under its own hash is a no-op, same as
            // the filesystem store. A lost race just uploads the same bytes twice — harmless.
            if (!await ExistsAsync(sha, ct))
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = sha,
                    FilePath = temp,
                }, ct);

            return new BlobResult(sha, size);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public async Task<bool> ExistsAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, sha256, ct);
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default)
    {
        GetObjectResponse response;
        try
        {
            response = await s3.GetObjectAsync(bucket, sha256, ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Same exception type as FileSystemBlobStore, so callers cannot tell the backends apart.
            throw new FileNotFoundException("Blob not found", sha256, e);
        }
        return new ResponseStream(response);
    }

    // GetObjectResponse owns the network stream; disposing only the inner stream leaks the response
    // (and with it the connection). Callers get one Stream and dispose one Stream — this makes that
    // one dispose reach both.
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
