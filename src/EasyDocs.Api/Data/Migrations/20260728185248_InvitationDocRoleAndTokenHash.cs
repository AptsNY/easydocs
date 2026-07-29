using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EasyDocs.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InvitationDocRoleAndTokenHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Token",
                table: "Invitations",
                newName: "TokenHash");

            migrationBuilder.AddColumn<string>(
                name: "DocRole",
                table: "Invitations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "DocRole",
                table: "Invitations");

            migrationBuilder.RenameColumn(
                name: "TokenHash",
                table: "Invitations",
                newName: "Token");
        }
    }
}
