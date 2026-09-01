using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchedFromMessageId",
                schema: "aifarm",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentConversationId",
                schema: "aifarm",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_BranchedFromMessageId",
                schema: "aifarm",
                table: "conversations",
                column: "BranchedFromMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_ParentConversationId",
                schema: "aifarm",
                table: "conversations",
                column: "ParentConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_conversations_ParentConversationId",
                schema: "aifarm",
                table: "conversations",
                column: "ParentConversationId",
                principalSchema: "aifarm",
                principalTable: "conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_conversations_ParentConversationId",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_BranchedFromMessageId",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_ParentConversationId",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "BranchedFromMessageId",
                schema: "aifarm",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "ParentConversationId",
                schema: "aifarm",
                table: "conversations");
        }
    }
}
