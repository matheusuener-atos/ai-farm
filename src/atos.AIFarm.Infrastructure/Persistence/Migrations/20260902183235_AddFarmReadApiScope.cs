using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmReadApiScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens");

            migrationBuilder.AddCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens",
                sql: "\"Scopes\" > 0 AND \"Scopes\" <= 127");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens");

            migrationBuilder.AddCheckConstraint(
                name: "CK_api_tokens_scopes",
                schema: "aifarm",
                table: "api_tokens",
                sql: "\"Scopes\" > 0 AND \"Scopes\" <= 63");
        }
    }
}
