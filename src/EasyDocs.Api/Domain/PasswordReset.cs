namespace EasyDocs.Api.Domain;

// An admin-issued, single-use password reset (spec 2026-09-08). easydocs has no mailer, so the link
// is delivered out of band by the admin who minted it — the same shape as an invitation.
//
// Mirrors Invitation minus the parts a reset does not need: no IssuedBy (the password_reset.issued
// audit event records the actor) and no RevokedAt (re-issuing stamps UsedAt on the outstanding row —
// one state machine, not two).
public class PasswordReset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid OrgId { get; set; }

    // Capability token, hashed at rest like invitations and share links; the raw token is returned once.
    public string TokenHash { get; set; } = null!;

    // Non-nullable where Invitation.ExpiresAt is nullable: an invitation that never expires is a
    // defensible convenience, a password reset that never expires is a permanent skeleton key.
    public DateTimeOffset ExpiresAt { get; set; }

    // Consumed OR superseded by a re-issue. Both mean "this link is dead".
    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
