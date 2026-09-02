using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalIncidentAcknowledgements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operational_incident_acknowledgements",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operational_incident_acknowledgements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_operational_incident_acknowledgements_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operational_incident_acknowledgements_AccountId_IncidentKey",
                schema: "aifarm",
                table: "operational_incident_acknowledgements",
                columns: new[] { "AccountId", "IncidentKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operational_incident_acknowledgements",
                schema: "aifarm");
        }
    }
}
