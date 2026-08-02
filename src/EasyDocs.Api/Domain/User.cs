namespace EasyDocs.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string? PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // MFA (issue #10). A secret with TotpEnabledAt null is a pending setup — created by
    // /account/mfa/setup, armed only when /account/mfa/enable proves the authenticator has it.
    // Recovery codes are stored as SHA-256 hashes and removed as they are spent.
    public string? TotpSecret { get; set; }
    public DateTimeOffset? TotpEnabledAt { get; set; }
    public string[] RecoveryCodeHashes { get; set; } = [];
}
