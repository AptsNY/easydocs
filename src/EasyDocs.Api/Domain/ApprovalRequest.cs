namespace EasyDocs.Api.Domain;

// IKeyed only declares (Id, CreatedAt), both of which already existed — it makes the row usable with
// Pagination.PageAsync for the approvals inbox. No schema change.
public class ApprovalRequest : IKeyed
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; }
    public Guid ApproverId { get; set; }
    public Guid RequestedBy { get; set; }
    public string? Decision { get; set; }
    public string? DecisionComment { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
