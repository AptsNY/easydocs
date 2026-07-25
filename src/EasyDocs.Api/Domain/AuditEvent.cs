namespace EasyDocs.Api.Domain;

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = null!;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
