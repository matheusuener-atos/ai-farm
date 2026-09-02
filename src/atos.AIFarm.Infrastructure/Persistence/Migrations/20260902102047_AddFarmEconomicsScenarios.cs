using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmEconomicsScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "farm_economics_scenarios",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    InputPayload = table.Column<string>(type: "text", nullable: false),
                    ResultPayload = table.Column<string>(type: "text", nullable: false),
                    SourceJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceEvidenceDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_farm_economics_scenarios", x => x.Id);
                    table.CheckConstraint("CK_farm_economics_digest", "length(\"Digest\") = 64");
                    table.ForeignKey(
                        name: "FK_farm_economics_scenarios_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_farm_economics_scenarios_farm_jobs_SourceJobId",
                        column: x => x.SourceJobId,
                        principalSchema: "aifarm",
                        principalTable: "farm_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_farm_economics_scenarios_AccountId_CreatedAtUtc",
                schema: "aifarm",
                table: "farm_economics_scenarios",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_farm_economics_scenarios_SourceJobId",
                schema: "aifarm",
                table: "farm_economics_scenarios",
                column: "SourceJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "farm_economics_scenarios",
                schema: "aifarm");
        }
    }
}
