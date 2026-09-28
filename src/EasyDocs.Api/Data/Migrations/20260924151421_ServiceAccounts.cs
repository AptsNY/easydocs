using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EasyDocs.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ManagedBy",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagedBy",
                table: "Users",
                column: "ManagedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ManagedBy",
                table: "Users",
                column: "ManagedBy",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without ManagedBy a service account is just a passwordless user, and pre-service-account code
            // has no RequirePerson: its live tokens could switch-org into a session. Kill them first.
            migrationBuilder.Sql(
                "UPDATE \"ApiTokens\" SET \"RevokedAt\" = now() WHERE \"RevokedAt\" IS NULL " +
                "AND \"UserId\" IN (SELECT \"Id\" FROM \"Users\" WHERE \"ManagedBy\" IS NOT NULL);");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ManagedBy",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_ManagedBy",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ManagedBy",
                table: "Users");
        }
    }
}
