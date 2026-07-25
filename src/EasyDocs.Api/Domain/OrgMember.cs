namespace EasyDocs.Api.Domain;

public class OrgMember
{
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public OrgRole Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
