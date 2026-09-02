using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmAgentJobLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentLeaseExpiresAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentLeaseRenewedAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CancellationRequestedAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_jobs_agent_lease",
                schema: "aifarm",
                table: "farm_jobs",
                sql: "(\"AgentLeaseRenewedAtUtc\" IS NULL AND \"AgentLeaseExpiresAtUtc\" IS NULL) OR (\"AgentLeaseRenewedAtUtc\" IS NOT NULL AND \"AgentLeaseExpiresAtUtc\" IS NOT NULL AND \"AgentLeaseExpiresAtUtc\" > \"AgentLeaseRenewedAtUtc\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_jobs_agent_lease",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "AgentLeaseExpiresAtUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "AgentLeaseRenewedAtUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "CancellationRequestedAtUtc",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
