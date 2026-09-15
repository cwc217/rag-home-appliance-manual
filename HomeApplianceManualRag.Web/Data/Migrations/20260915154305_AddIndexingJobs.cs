using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexingJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentVersion",
                table: "ManualDocuments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "IndexingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersion = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndexingJobs_ManualDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "ManualDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndexingJobs_DocumentId",
                table: "IndexingJobs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexingJobs_Status_NextAttemptAtUtc",
                table: "IndexingJobs",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.Sql("""
                INSERT INTO "IndexingJobs"
                    ("Id", "DocumentId", "DocumentVersion", "Status", "Attempts", "CreatedAtUtc", "NextAttemptAtUtc")
                SELECT
                    md5("Id"::text || ':phase3-indexing')::uuid,
                    "Id",
                    "DocumentVersion",
                    0,
                    0,
                    NOW(),
                    NOW()
                FROM "ManualDocuments"
                WHERE "Status" = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexingJobs");

            migrationBuilder.DropColumn(
                name: "DocumentVersion",
                table: "ManualDocuments");
        }
    }
}
