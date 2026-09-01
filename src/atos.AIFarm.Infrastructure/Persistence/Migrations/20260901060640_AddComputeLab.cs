using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddComputeLab : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "farm_nodes",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    VerificationLevel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Runtime = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LogicalProcessors = table.Column<int>(type: "integer", nullable: false),
                    MemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_nodes", x => x.Id);
                    table.CheckConstraint("CK_farm_nodes_status", "\"Status\" IN ('Online', 'Paused')");
                    table.CheckConstraint("CK_farm_nodes_verification", "\"VerificationLevel\" IN ('Detected', 'Verified')");
                    table.ForeignKey(
                        name: "FK_farm_nodes_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "farm_jobs",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Workload = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    WorkloadVersion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    VerificationPolicy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ExpectedDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ComputeUnitsMilli = table.Column<long>(type: "bigint", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_jobs", x => x.Id);
                    table.CheckConstraint("CK_farm_jobs_cu", "\"ComputeUnitsMilli\" >= 0");
                    table.CheckConstraint("CK_farm_jobs_status", "\"Status\" IN ('Queued', 'Running', 'Verifying', 'Completed', 'Failed')");
                    table.ForeignKey(
                        name: "FK_farm_jobs_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_jobs_farm_nodes_NodeId",
                        column: x => x.NodeId,
                        principalSchema: "aifarm",
                        principalTable: "farm_nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "farm_job_events",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_job_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_farm_job_events_farm_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "aifarm",
                        principalTable: "farm_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_job_events_JobId_Sequence",
                schema: "aifarm",
                table: "farm_job_events",
                columns: new[] { "JobId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_AccountId_CreatedAtUtc",
                schema: "aifarm",
                table: "farm_jobs",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_jobs_NodeId",
                schema: "aifarm",
                table: "farm_jobs",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_farm_nodes_AccountId",
                schema: "aifarm",
                table: "farm_nodes",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_job_events",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "farm_jobs",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "farm_nodes",
                schema: "aifarm");
        }
    }
}
