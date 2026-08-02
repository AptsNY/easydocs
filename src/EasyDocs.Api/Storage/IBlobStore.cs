namespace EasyDocs.Api.Storage;

public interface IBlobStore
{
    Task<BlobResult> PutAsync(Stream content, CancellationToken ct = default);
    Task<bool> ExistsAsync(string sha256, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default);
    /// <summary>Removes the stored bytes. Idempotent: deleting a blob that is not there is a no-op —
    /// the garbage collector retries sweeps after crashes and must not trip over its own progress.</summary>
    Task DeleteAsync(string sha256, CancellationToken ct = default);
}

public readonly record struct BlobResult(string Sha256, long SizeBytes);
