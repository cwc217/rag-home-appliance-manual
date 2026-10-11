using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowNullLegacyChunkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "LegacyPageNumber",
                table: "ManualChunks",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "LegacyText",
                table: "ManualChunks",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "ManualChunks"
                SET "LegacyText" = COALESCE("LegacyText", "Content"),
                    "LegacyPageNumber" = COALESCE("LegacyPageNumber", "PageNumbers"[1], 0)
                WHERE "LegacyText" IS NULL OR "LegacyPageNumber" IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "LegacyPageNumber",
                table: "ManualChunks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "LegacyText",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
