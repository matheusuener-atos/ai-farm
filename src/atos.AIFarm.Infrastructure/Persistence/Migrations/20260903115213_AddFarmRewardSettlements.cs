using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmRewardSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "farm_reward_budget_periods",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BudgetMinor = table.Column<long>(type: "bigint", nullable: false),
                    SpentMinor = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_reward_budget_periods", x => x.Id);
                    table.CheckConstraint("CK_farm_reward_budget_period_amounts", "\"BudgetMinor\" > 0 AND \"SpentMinor\" >= 0 AND \"SpentMinor\" <= \"BudgetMinor\"");
                });

            migrationBuilder.CreateTable(
                name: "farm_reward_settlements",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    LedgerTransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PeriodStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessingPointsMilli = table.Column<long>(type: "bigint", nullable: false),
                    AicMinorPerThousandCp = table.Column<long>(type: "bigint", nullable: false),
                    RewardAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    ProposalDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceEvidenceDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SettledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_reward_settlements", x => x.Id);
                    table.CheckConstraint("CK_farm_reward_settlement_amount", "\"RewardAmountMinor\" > 0");
                    table.CheckConstraint("CK_farm_reward_settlement_digest", "length(\"Digest\") = 64");
                    table.CheckConstraint("CK_farm_reward_settlement_points", "\"ProcessingPointsMilli\" > 0");
                    table.CheckConstraint("CK_farm_reward_settlement_rate", "\"AicMinorPerThousandCp\" > 0");
                    table.ForeignKey(
                        name: "FK_farm_reward_settlements_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_settlements_farm_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "aifarm",
                        principalTable: "farm_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_settlements_farm_reward_budget_periods_BudgetPe~",
                        column: x => x.BudgetPeriodId,
                        principalSchema: "aifarm",
                        principalTable: "farm_reward_budget_periods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_settlements_farm_reward_proposals_ProposalId",
                        column: x => x.ProposalId,
                        principalSchema: "aifarm",
                        principalTable: "farm_reward_proposals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_reward_settlements_ledger_transactions_LedgerTransacti~",
                        column: x => x.LedgerTransactionId,
                        principalSchema: "aifarm",
                        principalTable: "ledger_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_budget_periods_PolicyVersion_PeriodStartUtc",
                schema: "aifarm",
                table: "farm_reward_budget_periods",
                columns: new[] { "PolicyVersion", "PeriodStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_settlements_AccountId_SettledAtUtc",
                schema: "aifarm",
                table: "farm_reward_settlements",
                columns: new[] { "AccountId", "SettledAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_settlements_BudgetPeriodId",
                schema: "aifarm",
                table: "farm_reward_settlements",
                column: "BudgetPeriodId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_settlements_JobId",
                schema: "aifarm",
                table: "farm_reward_settlements",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_settlements_LedgerTransactionId",
                schema: "aifarm",
                table: "farm_reward_settlements",
                column: "LedgerTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_reward_settlements_ProposalId",
                schema: "aifarm",
                table: "farm_reward_settlements",
                column: "ProposalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_reward_settlements",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "farm_reward_budget_periods",
                schema: "aifarm");
        }
    }
}
