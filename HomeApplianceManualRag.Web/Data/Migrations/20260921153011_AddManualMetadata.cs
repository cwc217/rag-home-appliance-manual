using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddManualMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplianceCategory",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Brand",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MetadataConfidence",
                table: "ManualDocuments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataExtractedAtUtc",
                table: "ManualDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataStatus",
                table: "ManualDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelNumber",
                table: "ManualDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductName",
                table: "ManualDocuments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplianceCategory",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "Brand",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "MetadataConfidence",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "MetadataExtractedAtUtc",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "MetadataStatus",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "ModelNumber",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "ProductName",
                table: "ManualDocuments");
        }
    }
}
