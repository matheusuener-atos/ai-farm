using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistConversationsAndMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                schema: "aifarm",
                table: "ai_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conversations_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO aifarm.conversations
                    ("Id", "AccountId", "Status", "CreatedAtUtc", "UpdatedAtUtc")
                SELECT
                    "ConversationId",
                    "AccountId",
                    'Active',
                    MIN("CreatedAtUtc"),
                    MAX("CreatedAtUtc")
                FROM aifarm.ai_requests
                GROUP BY "ConversationId", "AccountId"
                ON CONFLICT ("Id") DO NOTHING;

                UPDATE aifarm.ai_requests
                SET "OperationId" = "ConversationId"
                WHERE "OperationId" = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiExecutionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_messages_ai_executions_AiExecutionId",
                        column: x => x.AiExecutionId,
                        principalSchema: "aifarm",
                        principalTable: "ai_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "aifarm",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_requests_ConversationId",
                schema: "aifarm",
                table: "ai_requests",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_AccountId_UpdatedAtUtc",
                schema: "aifarm",
                table: "conversations",
                columns: new[] { "AccountId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_messages_AiExecutionId",
                schema: "aifarm",
                table: "messages",
                column: "AiExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId_IdempotencyKey",
                schema: "aifarm",
                table: "messages",
                columns: new[] { "ConversationId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_requests_conversations_ConversationId",
                schema: "aifarm",
                table: "ai_requests",
                column: "ConversationId",
                principalSchema: "aifarm",
                principalTable: "conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_requests_conversations_ConversationId",
                schema: "aifarm",
                table: "ai_requests");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "aifarm");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "aifarm");

            migrationBuilder.DropIndex(
                name: "IX_ai_requests_ConversationId",
                schema: "aifarm",
                table: "ai_requests");

            migrationBuilder.DropColumn(
                name: "OperationId",
                schema: "aifarm",
                table: "ai_requests");
        }
    }
}
