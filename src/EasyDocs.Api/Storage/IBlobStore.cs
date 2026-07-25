namespace EasyDocs.Api.Storage;

public interface IBlobStore
{
    Task<BlobResult> PutAsync(Stream content, CancellationToken ct = default);
    Task<bool> ExistsAsync(string sha256, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default);
}

public readonly record struct BlobResult(string Sha256, long SizeBytes);
