using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualDocumentSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "ManualDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedByUserId",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionReason",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualDocuments_DeletedAtUtc",
                table: "ManualDocuments",
                column: "DeletedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualDocuments_DeletedAtUtc",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "DeletedByUserId",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "DeletionReason",
                table: "ManualDocuments");
        }
    }
}
