using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationAiSettingsAndPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaximumOutputTokens",
                schema: "aifarm",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 512);

            migrationBuilder.AddColumn<string>(
                name: "ModelPreference",
                schema: "aifarm",
                table: "conversations",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "auto");

            migrationBuilder.AddColumn<string>(
                name: "SystemInstruction",
                schema: "aifarm",
                table: "conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Temperature",
                schema: "aifarm",
                table: "conversations",
                type: "numeric(3,2)",
                precision: 3,
                scale: 2,
                nullable: false,
                defaultValue: 1.0m);

            migrationBuilder.CreateTable(
                name: "chat_presets",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ModelPreference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: "auto"),
                    SystemInstruction = table.Column<string>(type: "text", nullable: true),
                    Temperature = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false, defaultValue: 1.0m),
                    MaximumOutputTokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 512),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_presets", x => x.Id);
                    table.CheckConstraint("CK_chat_presets_maximum_output_tokens", "\"MaximumOutputTokens\" >= 64 AND \"MaximumOutputTokens\" <= 128000");
                    table.CheckConstraint("CK_chat_presets_system_instruction_length", "\"SystemInstruction\" IS NULL OR length(\"SystemInstruction\") <= 8000");
                    table.CheckConstraint("CK_chat_presets_temperature", "\"Temperature\" >= 0 AND \"Temperature\" <= 2");
                    table.ForeignKey(
                        name: "FK_chat_presets_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_maximum_output_tokens",
                schema: "aifarm",
                table: "conversations",
                sql: "\"MaximumOutputTokens\" >= 64 AND \"MaximumOutputTokens\" <= 128000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_system_instruction_length",
                schema: "aifarm",
                table: "conversations",
                sql: "\"SystemInstruction\" IS NULL OR length(\"SystemInstruction\") <= 8000");

            migrationBuilder.AddCheckConstraint(
                name: "CK_conversations_temperature",
                schema: "aifarm",
                table: "conversations",
                sql: "\"Temperature\" >= 0 AND \"Temperature\" <= 2");

            migrationBuilder.CreateIndex(
                name: "IX_chat_presets_AccountId_NormalizedName",
                schema: "aifarm",
                table: "chat_presets",
                columns: new[] { "AccountId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_presets",
                schema: "aifarm");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_maximum_output_tokens",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_system_instruction_length",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_conversations_temperature",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "MaximumOutputTokens",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ModelPreference",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "SystemInstruction",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Temperature",
                schema: "aifarm",
                table: "conversations");
        }
    }
}
