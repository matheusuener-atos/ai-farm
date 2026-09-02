using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiUsageBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_usage_budget_policies",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaximumPerRequestMinor = table.Column<long>(type: "bigint", nullable: false),
                    DailyLimitMinor = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyLimitMinor = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_budget_policies", x => x.Id);
                    table.CheckConstraint("CK_ai_usage_budget_policy_limits", "\"MaximumPerRequestMinor\" >= 0 AND \"DailyLimitMinor\" >= 0 AND \"MonthlyLimitMinor\" >= 0");
                    table.ForeignKey(
                        name: "FK_ai_usage_budget_policies_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_budget_reservations",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReservedMinor = table.Column<long>(type: "bigint", nullable: false),
                    ActualMinor = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_usage_budget_reservations", x => x.Id);
                    table.CheckConstraint("CK_ai_usage_budget_reservation_amounts", "\"ReservedMinor\" > 0 AND \"ActualMinor\" >= 0 AND \"ActualMinor\" <= \"ReservedMinor\"");
                    table.CheckConstraint("CK_ai_usage_budget_reservation_status", "\"Status\" IN ('Active', 'Settled', 'Released')");
                    table.ForeignKey(
                        name: "FK_ai_usage_budget_reservations_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_budget_policies_AccountId",
                schema: "aifarm",
                table: "ai_usage_budget_policies",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_budget_reservations_AccountId_IdempotencyKey",
                schema: "aifarm",
                table: "ai_usage_budget_reservations",
                columns: new[] { "AccountId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_budget_reservations_AccountId_OperationId",
                schema: "aifarm",
                table: "ai_usage_budget_reservations",
                columns: new[] { "AccountId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_usage_budget_reservations_AccountId_Status_ExpiresAtUtc",
                schema: "aifarm",
                table: "ai_usage_budget_reservations",
                columns: new[] { "AccountId", "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_usage_budget_policies",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "ai_usage_budget_reservations",
                schema: "aifarm");
        }
    }
}
