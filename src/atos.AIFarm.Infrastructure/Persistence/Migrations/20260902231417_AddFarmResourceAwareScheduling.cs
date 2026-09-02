using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFarmResourceAwareScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequiredLogicalProcessors",
                schema: "aifarm",
                table: "farm_demands",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "RequiredMemoryBytes",
                schema: "aifarm",
                table: "farm_demands",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "WorkloadVersion",
                schema: "aifarm",
                table: "farm_demands",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                "UPDATE aifarm.farm_demands SET \"RequiredLogicalProcessors\" = 1, " +
                "\"RequiredMemoryBytes\" = 134217728, " +
                "\"WorkloadVersion\" = 'atos-iris-text-index-v1' " +
                "WHERE \"WorkloadId\" = 'iris-text-index-v1'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_farm_demands_resources",
                schema: "aifarm",
                table: "farm_demands",
                sql: "\"RequiredLogicalProcessors\" > 0 AND \"RequiredMemoryBytes\" > 0 AND length(\"WorkloadVersion\") > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_farm_demands_resources",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "RequiredLogicalProcessors",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "RequiredMemoryBytes",
                schema: "aifarm",
                table: "farm_demands");

            migrationBuilder.DropColumn(
                name: "WorkloadVersion",
                schema: "aifarm",
                table: "farm_demands");
        }
    }
}
