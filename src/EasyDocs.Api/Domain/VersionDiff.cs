namespace EasyDocs.Api.Domain;

public class VersionDiff
{
    public string FromSha256 { get; set; } = null!;
    public string ToSha256 { get; set; } = null!;
    public int? Insertions { get; set; }
    public int? Deletions { get; set; }
    public int? Moves { get; set; }
    public int? FormatChanges { get; set; }
    public string? RedlineBlobSha256 { get; set; }
    public string? HtmlBlobSha256 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
