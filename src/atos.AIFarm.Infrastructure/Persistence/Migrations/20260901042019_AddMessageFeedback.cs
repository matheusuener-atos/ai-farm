using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "message_feedback",
                schema: "aifarm",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_feedback", x => x.Id);
                    table.CheckConstraint("CK_message_feedback_rating", "\"Rating\" IN ('Positive', 'Negative')");
                    table.CheckConstraint("CK_message_feedback_reason_length", "\"Reason\" IS NULL OR length(\"Reason\") <= 500");
                    table.ForeignKey(
                        name: "FK_message_feedback_accounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "aifarm",
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_message_feedback_messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "aifarm",
                        principalTable: "messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_message_feedback_AccountId_Rating",
                schema: "aifarm",
                table: "message_feedback",
                columns: new[] { "AccountId", "Rating" });

            migrationBuilder.CreateIndex(
                name: "IX_message_feedback_MessageId",
                schema: "aifarm",
                table: "message_feedback",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_feedback",
                schema: "aifarm");
        }
    }
}
