using System.Text;
using EasyDocs.Api.Storage;

public class BlobStoreTests
{
    [Fact]
    public async Task Put_returns_sha_and_stores_once_under_sharded_path()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var store = new FileSystemBlobStore(root);
        var bytes = Encoding.UTF8.GetBytes("hello docx");

        var r1 = await store.PutAsync(new MemoryStream(bytes));
        var r2 = await store.PutAsync(new MemoryStream(bytes)); // identical content

        Assert.Equal(r1.Sha256, r2.Sha256);                    // deterministic
        Assert.Equal(64, r1.Sha256.Length);                    // hex sha256
        Assert.Equal(bytes.Length, r1.SizeBytes);
        var path = Path.Combine(root, r1.Sha256[..2], r1.Sha256[2..4], r1.Sha256);
        Assert.True(File.Exists(path));                         // sharded layout
        Assert.True(await store.ExistsAsync(r1.Sha256));
        using var read = await store.OpenReadAsync(r1.Sha256);
        Assert.Equal(bytes, ReadAll(read));
    }

    [Fact]
    public async Task Put_is_write_once_second_identical_put_does_not_corrupt()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var store = new FileSystemBlobStore(root);
        var bytes = Encoding.UTF8.GetBytes("same content");
        var r1 = await store.PutAsync(new MemoryStream(bytes));
        var r2 = await store.PutAsync(new MemoryStream(bytes));
        Assert.Equal(r1.Sha256, r2.Sha256);
        using var read = await store.OpenReadAsync(r2.Sha256);
        Assert.Equal(bytes, ReadAll(read));
    }

    private static byte[] ReadAll(Stream s)
    { using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
}
