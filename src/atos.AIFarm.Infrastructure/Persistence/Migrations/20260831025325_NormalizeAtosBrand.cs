using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeAtosBrand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE aifarm.accounts
                SET "DisplayName" = replace(
                    "DisplayName",
                    chr(65) || chr(55) || chr(79) || chr(83),
                    'atos');

                UPDATE aifarm.ledger_entries
                SET "AccountCode" = replace(
                    "AccountCode",
                    chr(65) || chr(55) || chr(79) || chr(83) || ':',
                    'atos:');

                UPDATE aifarm.ai_executions
                SET "Provider" = replace(
                        "Provider",
                        chr(65) || chr(55) || chr(79) || chr(83),
                        'atos'),
                    "Model" = replace(
                        "Model",
                        chr(97) || chr(55) || chr(111) || chr(115),
                        'atos');

                UPDATE aifarm.ai_requests
                SET "RequestedRoute" = replace(
                    replace(
                        "RequestedRoute",
                        chr(65) || chr(55) || chr(79) || chr(83),
                        'atos'),
                    chr(97) || chr(55) || chr(111) || chr(115),
                    'atos')
                WHERE "RequestedRoute" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE aifarm.accounts
                SET "DisplayName" = replace(
                    "DisplayName",
                    'atos',
                    chr(65) || chr(55) || chr(79) || chr(83));

                UPDATE aifarm.ledger_entries
                SET "AccountCode" = replace(
                    "AccountCode",
                    'atos:',
                    chr(65) || chr(55) || chr(79) || chr(83) || ':');

                UPDATE aifarm.ai_executions
                SET "Provider" = replace(
                        "Provider",
                        'atos',
                        chr(65) || chr(55) || chr(79) || chr(83)),
                    "Model" = replace(
                        "Model",
                        'atos',
                        chr(97) || chr(55) || chr(111) || chr(115));

                UPDATE aifarm.ai_requests
                SET "RequestedRoute" = replace(
                    replace(
                        "RequestedRoute",
                        'atos Fake Provider',
                        chr(65) || chr(55) || chr(79) || chr(83) || ' Fake Provider'),
                    'atos',
                    chr(97) || chr(55) || chr(111) || chr(115))
                WHERE "RequestedRoute" IS NOT NULL;
                """);
        }
    }
}
