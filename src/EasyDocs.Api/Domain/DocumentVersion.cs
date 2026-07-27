namespace EasyDocs.Api.Domain;

public class DocumentVersion : IKeyed
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid BranchId { get; set; }
    public int SeqInBranch { get; set; }
    public Guid? ParentVersionId { get; set; }
    public Guid? MergeParentVersionId { get; set; }

    public int Major { get; set; }
    public int Minor { get; set; }
    public int Revision { get; set; }

    public string? Name { get; set; }
    public VersionSource Source { get; set; }

    // Publish state folded into versions (spec §4).
    public string? PublishedKind { get; set; }
    public Guid? PublishedBy { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? PublishName { get; set; }
    public string? PdfBlobSha256 { get; set; }

    public string BlobSha256 { get; set; } = null!;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
