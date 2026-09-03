using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountInterfaceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InterfaceMode",
                schema: "aifarm",
                table: "accounts",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "Lite");

            // Contas que já existiam continuam na experiência completa; só
            // contas novas nascem no Farmer Lite (ADR 0030).
            migrationBuilder.Sql(
                "UPDATE aifarm.accounts SET \"InterfaceMode\" = 'Pro'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_accounts_interface_mode",
                schema: "aifarm",
                table: "accounts",
                sql: "\"InterfaceMode\" IN ('Lite', 'Pro')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_accounts_interface_mode",
                schema: "aifarm",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "InterfaceMode",
                schema: "aifarm",
                table: "accounts");
        }
    }
}
