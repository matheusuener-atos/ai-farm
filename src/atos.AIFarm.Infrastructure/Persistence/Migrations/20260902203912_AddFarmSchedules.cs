using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                schema: "aifarm",
                table: "farm_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScheduledForUtc",
                schema: "aifarm",
                table: "farm_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "farm_schedules",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    WorkloadId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NextRunAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastTriggeredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_schedules", x => x.Id);
                    table.CheckConstraint("CK_farm_schedules_interval", "\"IntervalMinutes\" >= 5 AND \"IntervalMinutes\" <= 10080");
                    table.CheckConstraint("CK_farm_schedules_status", "\"Status\" IN ('Active', 'Paused')");
                    table.ForeignKey(
                        name: "FK_farm_schedules_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "farm_schedule_triggers",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduledForUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_schedule_triggers", x => x.Id);
                    table.CheckConstraint("CK_farm_schedule_triggers_status", "\"Status\" IN ('Dispatching', 'Submitted', 'Deferred', 'Failed')");
                    table.ForeignKey(
                        name: "FK_farm_schedule_triggers_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_schedule_triggers_farm_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "aifarm",
                        principalTable: "farm_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_schedule_triggers_farm_schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalSchema: "aifarm",
                        principalTable: "farm_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_ScheduleId_ScheduledForUtc",
                schema: "aifarm",
                table: "farm_jobs",
                columns: new[] { "ScheduleId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_schedule_triggers_AccountId_CreatedAtUtc",
                schema: "aifarm",
                table: "farm_schedule_triggers",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_schedule_triggers_JobId",
                schema: "aifarm",
                table: "farm_schedule_triggers",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_schedule_triggers_ScheduleId_ScheduledForUtc",
                schema: "aifarm",
                table: "farm_schedule_triggers",
                columns: new[] { "ScheduleId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_schedules_AccountId_NormalizedName",
                schema: "aifarm",
                table: "farm_schedules",
                columns: new[] { "AccountId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_schedules_Status_NextRunAtUtc",
                schema: "aifarm",
                table: "farm_schedules",
                columns: new[] { "Status", "NextRunAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_farm_jobs_farm_schedules_ScheduleId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "ScheduleId",
                principalSchema: "aifarm",
                principalTable: "farm_schedules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_farm_jobs_farm_schedules_ScheduleId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropTable(
                name: "farm_schedule_triggers",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "farm_schedules",
                schema: "aifarm");

            migrationBuilder.DropIndex(
                name: "IX_farm_jobs_ScheduleId_ScheduledForUtc",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                schema: "aifarm",
                table: "farm_jobs");

            migrationBuilder.DropColumn(
                name: "ScheduledForUtc",
                schema: "aifarm",
                table: "farm_jobs");
        }
    }
}
