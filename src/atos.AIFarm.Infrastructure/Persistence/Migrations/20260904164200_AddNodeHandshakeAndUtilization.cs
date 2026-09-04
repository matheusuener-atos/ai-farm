using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeHandshakeAndUtilization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmationCodeHash",
                schema: "aifarm",
                table: "farm_nodes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAtUtc",
                schema: "aifarm",
                table: "farm_nodes",
                type: "timestamp with time zone",
                nullable: true);

            // Nodes vinculados antes do handshake (ADR 0038) são selados como
            // confirmados: ninguém re-vincula à força.
            migrationBuilder.Sql(
                "UPDATE aifarm.farm_nodes SET \"ConfirmedAtUtc\" = now() WHERE \"ConnectionKind\" = 'Agent'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DecommissionedAtUtc",
                schema: "aifarm",
                table: "farm_nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastWorkerCpuPercent",
                schema: "aifarm",
                table: "farm_nodes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastWorkerMemoryBytes",
                schema: "aifarm",
                table: "farm_nodes",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmationCodeHash",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "DecommissionedAtUtc",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "LastWorkerCpuPercent",
                schema: "aifarm",
                table: "farm_nodes");

            migrationBuilder.DropColumn(
                name: "LastWorkerMemoryBytes",
                schema: "aifarm",
                table: "farm_nodes");
        }
    }
}
