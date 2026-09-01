using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Title",
                schema: "aifarm",
                table: "conversations",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE aifarm.conversations AS conversation
                SET "Title" = CASE
                    WHEN char_length(first_message.normalized_content) <= 80
                        THEN first_message.normalized_content
                    ELSE left(first_message.normalized_content, 79) || '…'
                END
                FROM (
                    SELECT DISTINCT ON ("ConversationId")
                        "ConversationId",
                        btrim(regexp_replace("Content", '\s+', ' ', 'g')) AS normalized_content
                    FROM aifarm.messages
                    WHERE "Role" = 'User'
                    ORDER BY "ConversationId", "CreatedAtUtc", "Id"
                ) AS first_message
                WHERE conversation."Id" = first_message."ConversationId"
                  AND conversation."Title" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Title",
                schema: "aifarm",
                table: "conversations");
        }
    }
}
