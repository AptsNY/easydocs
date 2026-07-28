namespace EasyDocs.Api.Domain;

public class Invitation
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Email { get; set; } = null!;
    public OrgRole Role { get; set; }
    public Guid? DocumentId { get; set; }

    // The document role to grant on accept, when DocumentId is set. An org-level Role alone cannot
    // express "invited as Editor on document X" — membership is strictly per-document (spec §11).
    public DocRole? DocRole { get; set; }

    // Capability token, hashed at rest like share links (spec §11); the raw token is returned once.
    public string TokenHash { get; set; } = null!;

    public Guid InvitedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
