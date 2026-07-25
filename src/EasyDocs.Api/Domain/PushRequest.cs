namespace EasyDocs.Api.Domain;

public class PushRequest
{
    public Guid Id { get; set; }
    public Guid CopyDocumentId { get; set; }
    public Guid TargetDocumentId { get; set; }
    public Guid SourceVersionId { get; set; }
    public string Status { get; set; } = null!;
    public Guid? MaterializedVersionId { get; set; }
    public Guid PushedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
