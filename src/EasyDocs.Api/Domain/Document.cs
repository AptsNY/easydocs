namespace EasyDocs.Api.Domain;

public class Document
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid? FolderId { get; set; }
    public string Name { get; set; } = null!;
    public Guid? ParentDocumentId { get; set; }
    public Guid? ForkedFromVersionId { get; set; }

    // Authoritative version counter (spec §5.1) — single source of truth for numbering.
    public int VersionCounterMajor { get; set; }
    public int VersionCounterMinor { get; set; }
    public int VersionCounterRev { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
