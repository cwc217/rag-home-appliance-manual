using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPageViewActivityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnonymousVisitorId",
                table: "UserActivities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "UserActivities",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserActivities_Path_CreatedAtUtc",
                table: "UserActivities",
                columns: new[] { "Path", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserActivities_Path_CreatedAtUtc",
                table: "UserActivities");

            migrationBuilder.DropColumn(
                name: "AnonymousVisitorId",
                table: "UserActivities");

            migrationBuilder.DropColumn(
                name: "Path",
                table: "UserActivities");
        }
    }
}
