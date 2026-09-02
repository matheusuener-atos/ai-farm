using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmAgentProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_status",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.AddColumn<string>(
                name: "AgentCredentialHash",
                schema: "aifarm",
                table: "farm_nodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AgentCredentialIssuedAtUtc",
                schema: "aifarm",
                table: "farm_nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentVersion",
                schema: "aifarm",
                table: "farm_nodes",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionKind",
                schema: "aifarm",
                table: "farm_nodes",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Embedded");

            migrationBuilder.CreateTable(
                name: "farm_agent_enrollments",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_agent_enrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_farm_agent_enrollments_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AgentCredentialHash",
                schema: "aifarm",
                table: "farm_nodes",
                column: "AgentCredentialHash",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_connection_kind",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "\"ConnectionKind\" IN ('Embedded', 'Agent')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_status",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "\"Status\" IN ('Online', 'Paused', 'Offline')");

            migrationBuilder.CreateIndex(
                name: "IX_farm_agent_enrollments_AccountId_ExpiresAtUtc",
                schema: "aifarm",
                table: "farm_agent_enrollments",
                columns: new[] { "AccountId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_agent_enrollments_TokenHash",
                schema: "aifarm",
                table: "farm_agent_enrollments",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_agent_enrollments",
                schema: "aifarm");

            migrationBuilder.DropIndex(
                name: "IX_farm_nodes_AgentCredentialHash",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_connection_kind",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_nodes_status",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentCredentialHash",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentCredentialIssuedAtUtc",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "AgentVersion",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "ConnectionKind",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_nodes_status",
                schema: "aifarm",
                table: "farm_nodes",
                sql: "\"Status\" IN ('Online', 'Paused')");
        }
    }
}
