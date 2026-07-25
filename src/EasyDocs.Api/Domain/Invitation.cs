namespace EasyDocs.Api.Domain;

public class Invitation
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Email { get; set; } = null!;
    public OrgRole Role { get; set; }
    public Guid? DocumentId { get; set; }
    public string Token { get; set; } = null!;
    public Guid InvitedBy { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
