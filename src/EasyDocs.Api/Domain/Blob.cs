namespace EasyDocs.Api.Domain;

public class Blob
{
    public string Sha256 { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string Mime { get; set; } = null!;
    public string StorageKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
