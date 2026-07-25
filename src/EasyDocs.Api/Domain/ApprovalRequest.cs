namespace EasyDocs.Api.Domain;

public class ApprovalRequest
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
