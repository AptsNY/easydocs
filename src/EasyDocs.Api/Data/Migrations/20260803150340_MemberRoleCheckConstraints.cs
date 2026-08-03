using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EasyDocs.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemberRoleCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_OrgMembers_Role",
                table: "OrgMembers",
                sql: "\"Role\" IN ('Owner','Admin','Member')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentMembers_Role",
                table: "DocumentMembers",
                sql: "\"Role\" IN ('Owner','Editor','Viewer')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrgMembers_Role",
                table: "OrgMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentMembers_Role",
                table: "DocumentMembers");
        }
    }
}
