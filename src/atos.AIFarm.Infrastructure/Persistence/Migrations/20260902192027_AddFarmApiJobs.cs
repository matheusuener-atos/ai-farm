using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmApiJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens");

            migrationBuilder.AddColumn<string>(
                name: "ApiIdempotencyKey",
                schema: "aifarm",
                table: "farm_jobs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApiTokenId",
                schema: "aifarm",
                table: "farm_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_ApiTokenId_ApiIdempotencyKey",
                schema: "aifarm",
                table: "farm_jobs",
                columns: new[] { "ApiTokenId", "ApiIdempotencyKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_api_idempotency_complete",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"ApiTokenId\" IS NULL AND \"ApiIdempotencyKey\" IS NULL) OR (\"ApiTokenId\" IS NOT NULL AND \"ApiIdempotencyKey\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens",
                sql: "\"Scopes\" > 0 AND \"Scopes\" <= 255");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_ApiTokenId_ApiIdempotencyKey",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_api_idempotency_complete",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "ApiIdempotencyKey",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ApiTokenId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens",
                sql: "\"Scopes\" > 0 AND \"Scopes\" <= 127");
        }
    }
}
