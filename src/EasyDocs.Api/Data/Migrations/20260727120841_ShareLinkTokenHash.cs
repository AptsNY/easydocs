using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EasyDocs.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShareLinkTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Token",
                table: "ShareLinks",
                newName: "TokenHash");

            migrationBuilder.AddColumn<int>(
                name: "ViewCount",
                table: "ShareLinks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ShareLinks_TokenHash",
                table: "ShareLinks",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShareLinks_TokenHash",
                table: "ShareLinks");

            migrationBuilder.DropColumn(
                name: "ViewCount",
                table: "ShareLinks");

            migrationBuilder.RenameColumn(
                name: "TokenHash",
                table: "ShareLinks",
                newName: "Token");
        }
    }
}
