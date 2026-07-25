namespace EasyDocs.Api.Domain;

public class Folder
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = null!;
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
