namespace EasyDocs.Api.Domain;

// Public tokenized share of one version (E10). Only the token HASH is stored (spec §11).
public class ShareLink
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public string TokenHash { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int ViewCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
