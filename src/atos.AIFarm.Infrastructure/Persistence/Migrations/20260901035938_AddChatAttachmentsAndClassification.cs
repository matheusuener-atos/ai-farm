using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChatAttachmentsAndClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_attachments",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(700)", maxLength: 700, nullable: false),
                    FileName = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_attachments", x => x.Id);
                    table.CheckConstraint("CK_chat_attachments_sha256", "length(\"Sha256\") = 64");
                    table.CheckConstraint("CK_chat_attachments_size", "\"SizeBytes\" > 0");
                    table.CheckConstraint("CK_chat_attachments_status", "\"Status\" IN ('Draft', 'Attached')");
                    table.ForeignKey(
                        name: "FK_chat_attachments_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_attachments_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "aifarm",
                        principalTable: "conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_attachments_messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "aifarm",
                        principalTable: "messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_attachments_AccountId_ConversationId_Status",
                schema: "aifarm",
                table: "chat_attachments",
                columns: new[] { "AccountId", "ConversationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_attachments_ConversationId",
                schema: "aifarm",
                table: "chat_attachments",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_chat_attachments_MessageId",
                schema: "aifarm",
                table: "chat_attachments",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_attachments",
                schema: "aifarm");
        }
    }
}
