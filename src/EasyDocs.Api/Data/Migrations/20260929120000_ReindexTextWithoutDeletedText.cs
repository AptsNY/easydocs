using EasyDocs.Api.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EasyDocs.Api.Data.Migrations
{
    /// <summary>
    /// Data-only: DocxText used to index tracked-deleted wording and field codes, so every indexed
    /// document is re-queued for the TextIndexWorker ("extract" job, payload = the document id as a JSON
    /// string, exactly what BackgroundJobs.For serializes). No model change, hence no Designer snapshot.
    /// </summary>
    [DbContext(typeof(EasyDocsDbContext))]
    [Migration("20260929120000_ReindexTextWithoutDeletedText")]
    public partial class ReindexTextWithoutDeletedText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "INSERT INTO \"BackgroundJobs\" (\"Type\", \"Payload\", \"Attempts\", \"RunAfter\", \"CreatedAt\") " +
                "SELECT 'extract', '\"' || \"DocumentId\"::text || '\"', 0, now(), now() FROM \"DocumentTexts\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: a re-extract is idempotent, and the old index content is not worth restoring.
        }
    }
}
