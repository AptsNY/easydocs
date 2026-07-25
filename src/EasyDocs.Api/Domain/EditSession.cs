namespace EasyDocs.Api.Domain;

// WOPI-only in v1 — no `mode` column (spec §4, §6).
public class EditSession
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid BaseVersionId { get; set; }
    public Guid? BranchId { get; set; }
    public Guid UserId { get; set; }
    public string? LockValue { get; set; }
    public DateTimeOffset? LockExpiresAt { get; set; }
    public string? LastCommittedSha { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}
