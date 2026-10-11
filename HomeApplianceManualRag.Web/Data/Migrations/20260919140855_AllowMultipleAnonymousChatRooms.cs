using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleAnonymousChatRooms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations",
                column: "AnonymousAccessKeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations",
                column: "AnonymousAccessKeyHash",
                unique: true);
        }
    }
}
