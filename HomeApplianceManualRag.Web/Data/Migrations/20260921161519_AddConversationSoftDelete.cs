using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "Conversations");
        }
    }
}
