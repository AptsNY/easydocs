using EasyDocs.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EasyDocs.Api.Data;

public class EasyDocsDbContext(DbContextOptions<EasyDocsDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<OrgMember> OrgMembers => Set<OrgMember>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentMember> DocumentMembers => Set<DocumentMember>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Blob> Blobs => Set<Blob>();
    public DbSet<DocumentVersion> Versions => Set<DocumentVersion>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<PushRequest> PushRequests => Set<PushRequest>();
    public DbSet<EditSession> EditSessions => Set<EditSession>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<VersionDiff> VersionDiffs => Set<VersionDiff>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("citext");
        b.HasPostgresExtension("pgcrypto");

        // gen_random_uuid() DB-side defaults on every Guid PK.
        foreach (var et in b.Model.GetEntityTypes())
        {
            var id = et.FindProperty("Id");
            if (id is not null && id.ClrType == typeof(Guid) && et.FindPrimaryKey()?.Properties is [var pk] && pk == id)
                id.SetDefaultValueSql("gen_random_uuid()");
        }

        // Restrict everywhere: soft-delete (DeletedAt) + immutable versions mean a cascade
        // must never fire. Applies to self-refs too. No navigation properties (kept minimal).
        const DeleteBehavior R = DeleteBehavior.Restrict;

        b.Entity<Organization>().HasIndex(x => x.Slug).IsUnique();

        b.Entity<User>(e =>
        {
            e.Property(x => x.Email).HasColumnType("citext");
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<OrgMember>(e =>
        {
            e.HasKey(x => new { x.OrgId, x.UserId });
            e.Property(x => x.Role).HasConversion<string>();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(R);
        });

        b.Entity<Folder>(e =>
        {
            e.HasIndex(x => new { x.OrgId, x.ParentId, x.Name }).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(R);
        });

        b.Entity<Document>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(R);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.ParentDocumentId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.ForkedFromVersionId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(R);
        });

        b.Entity<DocumentMember>(e =>
        {
            e.HasKey(x => new { x.DocumentId, x.UserId });
            e.Property(x => x.Role).HasConversion<string>();
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(R);
        });

        b.Entity<Invitation>(e =>
        {
            e.Property(x => x.Role).HasConversion<string>();
            e.Property(x => x.DocRole).HasConversion<string>();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.InvitedBy).OnDelete(R);
        });

        b.Entity<Branch>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.DocumentId, x.Ordinal }).IsUnique();
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.RootVersionId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.MergedIntoVersionId).OnDelete(R);
        });

        b.Entity<Blob>().HasKey(x => x.Sha256);

        b.Entity<DocumentVersion>(e =>
        {
            e.Property(x => x.Source).HasConversion<string>();
            e.HasIndex(x => new { x.BranchId, x.SeqInBranch }).IsUnique();
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(R);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.ParentVersionId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.MergeParentVersionId).OnDelete(R);
            e.HasOne<Blob>().WithMany().HasForeignKey(x => x.BlobSha256).OnDelete(R);
            e.HasOne<Blob>().WithMany().HasForeignKey(x => x.PdfBlobSha256).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(R);
        });

        b.Entity<ApprovalRequest>(e =>
        {
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ApproverId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(R);
        });

        b.Entity<PushRequest>(e =>
        {
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.CopyDocumentId).OnDelete(R);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.TargetDocumentId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.SourceVersionId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.MaterializedVersionId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.PushedBy).OnDelete(R);
        });

        b.Entity<EditSession>(e =>
        {
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(R);
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.BaseVersionId).OnDelete(R);
            e.HasOne<Branch>().WithMany().HasForeignKey(x => x.BranchId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(R);
        });

        b.Entity<ShareLink>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<DocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(R);
        });

        b.Entity<VersionDiff>(e =>
        {
            e.HasKey(x => new { x.FromSha256, x.ToSha256 });
            e.HasOne<Blob>().WithMany().HasForeignKey(x => x.FromSha256).OnDelete(R);
            e.HasOne<Blob>().WithMany().HasForeignKey(x => x.ToSha256).OnDelete(R);
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(R);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(R);
        });

        // AuditEvent stays FK-light per spec §4/§11 (append-only; org/document/actor nullable, no hard FKs).
    }
}
