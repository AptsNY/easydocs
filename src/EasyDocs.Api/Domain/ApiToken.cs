namespace EasyDocs.Api.Domain;

// `ed_`-prefixed tokens, hashed at rest (spec §4, §11). user_id nullable for service accounts.
public class ApiToken
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? UserId { get; set; }
    public string? ServiceName { get; set; }
    public string TokenHash { get; set; } = null!;
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
