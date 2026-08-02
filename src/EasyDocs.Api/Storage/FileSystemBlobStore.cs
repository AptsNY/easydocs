using System.Security.Cryptography;

namespace EasyDocs.Api.Storage;

public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly string _tmp;

    public FileSystemBlobStore(string root)
    {
        _root = root;
        _tmp = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(_tmp);
    }

    public async Task<BlobResult> PutAsync(Stream content, CancellationToken ct = default)
    {
        var temp = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        long size;
        string sha;
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                size = 0;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var final = ShardPath(sha);
            if (File.Exists(final))
            {
                File.Delete(temp); // write-once: identical blob already stored
                return new BlobResult(sha, size);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(temp, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                File.Delete(temp); // lost a race; the existing blob is identical by content address
            }
            return new BlobResult(sha, size);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ShardPath(sha256)));

    public Task<Stream> OpenReadAsync(string sha256, CancellationToken ct = default)
    {
        var path = ShardPath(sha256);
        if (!File.Exists(path)) throw new FileNotFoundException("Blob not found", sha256);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task DeleteAsync(string sha256, CancellationToken ct = default)
    {
        var path = ShardPath(sha256);
        if (File.Exists(path)) File.Delete(path);
        // Shard directories are left behind empty; two levels of 256 dirs is noise, not leakage.
        return Task.CompletedTask;
    }

    private string ShardPath(string sha) => Path.Combine(_root, sha[..2], sha[2..4], sha);
}
