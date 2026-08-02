using NpgsqlTypes;

namespace EasyDocs.Api.Domain;

// One row per document: the extracted plain text of its main-branch head, plus the generated
// tsvector the dashboard's content search matches against (issue #12). Rewritten whole on every
// reindex — history is not searched, the current document is.
public class DocumentText
{
    public Guid DocumentId { get; set; }
    public string Text { get; set; } = null!;
    public NpgsqlTsVector SearchVector { get; set; } = null!; // computed by Postgres, never set here
    public DateTimeOffset UpdatedAt { get; set; }
}
