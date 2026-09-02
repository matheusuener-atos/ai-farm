using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmRewardProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "farm_reward_proposals",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessingPointsMilli = table.Column<long>(type: "bigint", nullable: false),
                    RecommendedRewardBrl = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    SourceEvidenceDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ScenarioDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_reward_proposals", x => x.Id);
                    table.CheckConstraint("CK_farm_reward_proposals_digest", "length(\"Digest\") = 64");
                    table.CheckConstraint("CK_farm_reward_proposals_points", "\"ProcessingPointsMilli\" > 0");
                    table.CheckConstraint("CK_farm_reward_proposals_reward", "\"RecommendedRewardBrl\" >= 0");
                    table.CheckConstraint("CK_farm_reward_proposals_status", "\"Status\" IN ('AwaitingPolicyAndFunding')");
                    table.ForeignKey(
                        name: "FK_farm_reward_proposals_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_proposals_farm_economics_scenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalSchema: "aifarm",
                        principalTable: "farm_economics_scenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_proposals_farm_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "aifarm",
                        principalTable: "farm_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_proposals_AccountId_CreatedAtUtc",
                schema: "aifarm",
                table: "farm_reward_proposals",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_proposals_AccountId_ScenarioId",
                schema: "aifarm",
                table: "farm_reward_proposals",
                columns: new[] { "AccountId", "ScenarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_proposals_JobId",
                schema: "aifarm",
                table: "farm_reward_proposals",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_proposals_ScenarioId",
                schema: "aifarm",
                table: "farm_reward_proposals",
                column: "ScenarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_reward_proposals",
                schema: "aifarm");
        }
    }
}
