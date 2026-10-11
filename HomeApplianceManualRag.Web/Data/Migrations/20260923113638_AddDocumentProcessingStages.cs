using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentProcessingStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessingStage",
                table: "ManualDocuments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProgressCompletedUnits",
                table: "ManualDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressTotalUnits",
                table: "ManualDocuments",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingStage",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "ProgressCompletedUnits",
                table: "ManualDocuments");

            migrationBuilder.DropColumn(
                name: "ProgressTotalUnits",
                table: "ManualDocuments");
        }
    }
}
