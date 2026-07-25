namespace EasyDocs.Api.Domain;

public class Branch
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public int Ordinal { get; set; }
    public Guid? RootVersionId { get; set; }
    public BranchKind Kind { get; set; }
    public Guid? MergedIntoVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
