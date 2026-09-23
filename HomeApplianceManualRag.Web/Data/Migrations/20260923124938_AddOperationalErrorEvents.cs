using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalErrorEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperationalErrorEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastOccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    Service = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    ExceptionType = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    Details = table.Column<string>(type: "text", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    IndexingJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalErrorEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalErrorEvents_Fingerprint_LastOccurredAtUtc",
                table: "OperationalErrorEvents",
                columns: new[] { "Fingerprint", "LastOccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalErrorEvents_LastOccurredAtUtc",
                table: "OperationalErrorEvents",
                column: "LastOccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_OperationalErrorEvents_Service_LastOccurredAtUtc",
                table: "OperationalErrorEvents",
                columns: new[] { "Service", "LastOccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperationalErrorEvents");
        }
    }
}
