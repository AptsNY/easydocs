namespace EasyDocs.Api.Domain;

public class ShareLink
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public string Token { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
