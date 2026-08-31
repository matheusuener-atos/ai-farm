using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace atos.AIFarm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRelationalIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_ai_requests_accounts_AccountId",
                schema: "aifarm",
                table: "ai_requests",
                column: "AccountId",
                principalSchema: "aifarm",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_balance_reservations_wallets_WalletId",
                schema: "aifarm",
                table: "balance_reservations",
                column: "WalletId",
                principalSchema: "aifarm",
                principalTable: "wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ledger_transactions_wallets_WalletId",
                schema: "aifarm",
                table: "ledger_transactions",
                column: "WalletId",
                principalSchema: "aifarm",
                principalTable: "wallets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_wallets_accounts_AccountId",
                schema: "aifarm",
                table: "wallets",
                column: "AccountId",
                principalSchema: "aifarm",
                principalTable: "accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_requests_accounts_AccountId",
                schema: "aifarm",
                table: "ai_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_balance_reservations_wallets_WalletId",
                schema: "aifarm",
                table: "balance_reservations");

            migrationBuilder.DropForeignKey(
                name: "FK_ledger_transactions_wallets_WalletId",
                schema: "aifarm",
                table: "ledger_transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_wallets_accounts_AccountId",
                schema: "aifarm",
                table: "wallets");
        }
    }
}
