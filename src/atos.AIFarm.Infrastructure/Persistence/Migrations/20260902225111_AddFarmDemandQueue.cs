using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmDemandQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DemandId",
                schema: "aifarm",
                table: "farm_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "farm_demands",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApiIdempotencyKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    WorkloadId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    InputVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InputPayload = table.Column<string>(type: "text", nullable: false),
                    InputSizeBytes = table.Column<int>(type: "integer", nullable: false),
                    InputDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_demands", x => x.Id);
                    table.CheckConstraint("CK_farm_demands_attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("CK_farm_demands_expiration", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
                    table.CheckConstraint("CK_farm_demands_priority", "\"Priority\" >= 0 AND \"Priority\" <= 2");
                    table.CheckConstraint("CK_farm_demands_status", "\"Status\" IN ('Queued', 'Dispatching', 'Submitted', 'Cancelled', 'Expired', 'Failed')");
                    table.ForeignKey(
                        name: "FK_farm_demands_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_DemandId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "DemandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_demands_AccountId_CreatedAtUtc",
                schema: "aifarm",
                table: "farm_demands",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_demands_ApiTokenId_ApiIdempotencyKey",
                schema: "aifarm",
                table: "farm_demands",
                columns: new[] { "ApiTokenId", "ApiIdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_demands_Status_NextAttemptAtUtc_Priority",
                schema: "aifarm",
                table: "farm_demands",
                columns: new[] { "Status", "NextAttemptAtUtc", "Priority" });

            migrationBuilder.AddForeignKey(
                name: "FK_farm_jobs_farm_demands_DemandId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "DemandId",
                principalSchema: "aifarm",
                principalTable: "farm_demands",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_farm_jobs_farm_demands_DemandId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropTable(
                name: "farm_demands",
                schema: "aifarm");

            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_DemandId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "DemandId",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
