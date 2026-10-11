using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace HomeApplianceManualRag.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStructureAwareChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualChunks_DocumentId",
                table: "ManualChunks");

            migrationBuilder.RenameColumn(
                name: "Text",
                table: "ManualChunks",
                newName: "LegacyText");

            migrationBuilder.RenameColumn(
                name: "PageNumber",
                table: "ManualChunks",
                newName: "LegacyPageNumber");

            migrationBuilder.AddColumn<string>(
                name: "ChunkType",
                table: "ManualChunks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Text");

            migrationBuilder.AddColumn<string>(
                name: "ChunkerVersion",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "legacy-unknown");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingText",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LanguageCode",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "ManualChunks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "PageNumbers",
                table: "ManualChunks",
                type: "integer[]",
                nullable: false,
                defaultValue: new int[0]);

            migrationBuilder.AddColumn<int>(
                name: "TokenCount",
                table: "ManualChunks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentChunkId",
                table: "ManualChunks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "ParentHeaders",
                table: "ManualChunks",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "ParserVersion",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "legacy");

            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "ManualChunks",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "SearchVector",
                table: "ManualChunks",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple'::regconfig, \"SearchText\")",
                stored: true);

            migrationBuilder.Sql("""
                UPDATE "ManualChunks"
                SET "Content" = "LegacyText",
                    "EmbeddingText" = "LegacyText",
                    "SearchText" = "LegacyText",
                    "PageNumbers" = CASE
                        WHEN "LegacyPageNumber" > 0 THEN ARRAY["LegacyPageNumber"]
                        ELSE ARRAY[]::integer[]
                    END,
                    "TokenCount" = GREATEST(1, CEIL(char_length("LegacyText") / 4.0)::integer),
                    "LanguageCode" = 'und';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ManualChunks_DocumentId_ChunkOrdinal",
                table: "ManualChunks",
                columns: new[] { "DocumentId", "ChunkOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualChunks_Embedding",
                table: "ManualChunks",
                column: "Embedding",
                filter: "\"Embedding\" IS NOT NULL")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_ManualChunks_ParentChunkId",
                table: "ManualChunks",
                column: "ParentChunkId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualChunks_SearchVector",
                table: "ManualChunks",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.AddForeignKey(
                name: "FK_ManualChunks_ManualChunks_ParentChunkId",
                table: "ManualChunks",
                column: "ParentChunkId",
                principalTable: "ManualChunks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ManualChunks_ManualChunks_ParentChunkId",
                table: "ManualChunks");

            migrationBuilder.DropIndex(
                name: "IX_ManualChunks_DocumentId_ChunkOrdinal",
                table: "ManualChunks");

            migrationBuilder.DropIndex(
                name: "IX_ManualChunks_Embedding",
                table: "ManualChunks");

            migrationBuilder.DropIndex(
                name: "IX_ManualChunks_ParentChunkId",
                table: "ManualChunks");

            migrationBuilder.DropIndex(
                name: "IX_ManualChunks_SearchVector",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "SearchVector",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "ChunkType",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "ChunkerVersion",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "EmbeddingText",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "LanguageCode",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "PageNumbers",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "ParentChunkId",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "ParentHeaders",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "ParserVersion",
                table: "ManualChunks");

            migrationBuilder.DropColumn(
                name: "TokenCount",
                table: "ManualChunks");

            migrationBuilder.RenameColumn(
                name: "LegacyPageNumber",
                table: "ManualChunks",
                newName: "PageNumber");

            migrationBuilder.RenameColumn(
                name: "LegacyText",
                table: "ManualChunks",
                newName: "Text");

            migrationBuilder.CreateIndex(
                name: "IX_ManualChunks_DocumentId",
                table: "ManualChunks",
                column: "DocumentId");
        }
    }
}
