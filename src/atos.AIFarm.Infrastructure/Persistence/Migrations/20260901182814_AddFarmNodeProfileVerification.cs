using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmNodeProfileVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProfileVerifiedAtUtc",
                schema: "aifarm",
                table: "farm_nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedProfileHash",
                schema: "aifarm",
                table: "farm_nodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE aifarm.farm_nodes
                SET "VerificationLevel" = 'Detected'
                WHERE "VerificationLevel" = 'Verified';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_verified_profile",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "(\"VerificationLevel\" = 'Detected' AND \"VerifiedProfileHash\" IS NULL AND \"ProfileVerifiedAtUtc\" IS NULL) OR (\"VerificationLevel\" = 'Verified' AND \"VerifiedProfileHash\" IS NOT NULL AND length(\"VerifiedProfileHash\") = 64 AND \"ProfileVerifiedAtUtc\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_verified_profile",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "ProfileVerifiedAtUtc",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "VerifiedProfileHash",
                schema: "aifarm",
                table: "farm_nodes");
        }
    }
}
