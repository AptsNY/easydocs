namespace EasyDocs.Api.Domain;

public class DocumentMember
{
    public Guid DocumentId { get; set; }
    public Guid UserId { get; set; }
    public DocRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
