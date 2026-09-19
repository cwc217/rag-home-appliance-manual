using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatRoomsAndAnonymousRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Conversations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "AnonymousAccessKeyHash",
                table: "Conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Conversations",
                type: "text",
                nullable: false,
                defaultValue: "New chat");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations",
                column: "AnonymousAccessKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_AnonymousAccessKeyHash",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AnonymousAccessKeyHash",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Conversations");

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Conversations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_UserId",
                table: "Conversations",
                column: "UserId",
                unique: true);
        }
    }
}
