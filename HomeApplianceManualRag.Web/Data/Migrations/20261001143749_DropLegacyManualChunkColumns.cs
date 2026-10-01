using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyManualChunkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LegacyPageNumber",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "LegacyText",
                table: "ManualChunks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LegacyPageNumber",
                table: "ManualChunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegacyText",
                table: "ManualChunks",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "ManualChunks"
                SET "LegacyText" = "Content",
                    "LegacyPageNumber" = "PageNumbers"[1];
                """);
        }
    }
}
