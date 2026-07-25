namespace EasyDocs.Api.Domain;

public class Organization
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
