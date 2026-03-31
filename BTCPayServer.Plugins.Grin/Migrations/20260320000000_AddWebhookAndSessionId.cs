using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Grin.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookAndSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookUrl",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinStoreSettings",
                type: "text",
                nullable: true,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "WebhookSecret",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinStoreSettings",
                type: "text",
                nullable: true,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrderId",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedirectUrl",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "WebhookUrl", schema: "BTCPayServer.Plugins.Grin", table: "GrinStoreSettings");
            migrationBuilder.DropColumn(name: "WebhookSecret", schema: "BTCPayServer.Plugins.Grin", table: "GrinStoreSettings");
            migrationBuilder.DropColumn(name: "SessionId", schema: "BTCPayServer.Plugins.Grin", table: "GrinInvoices");
            migrationBuilder.DropColumn(name: "OrderId", schema: "BTCPayServer.Plugins.Grin", table: "GrinInvoices");
            migrationBuilder.DropColumn(name: "RedirectUrl", schema: "BTCPayServer.Plugins.Grin", table: "GrinInvoices");
        }
    }
}
