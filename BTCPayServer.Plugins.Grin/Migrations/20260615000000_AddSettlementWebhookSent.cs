using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Grin.Migrations
{
    /// <summary>
    /// Adds the cross-process atomic guard for InvoicePaymentSettled
    /// dispatch. The plugin has two paths that can promote an invoice
    /// from Broadcast → Confirmed: the customer-side /status poll
    /// (fires every 5s during the checkout-page lifetime) and the
    /// background <c>GrinPaymentMonitorService</c> (fires every 30s).
    /// Without a DB-level guard, both can fire the settlement webhook
    /// on the same confirmation event — duplicate captures at the
    /// merchant. With the guard, only the call whose UPDATE returns
    /// affected-rows=1 owns the dispatch.
    ///
    /// The column defaults to false so any existing invoices stay
    /// unsignaled — the monitor's new <c>RetryUnsignaledSettlements</c>
    /// loop will catch any rows that confirmed before this migration
    /// landed but never notified the merchant. That's deliberate: it
    /// closes the historical exposure to the same race that the 3
    /// stuck staging Grin orders hit on 2026-06-15.
    /// </summary>
    public partial class AddSettlementWebhookSent : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SettlementWebhookSent",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill pre-existing Confirmed rows as already-signaled.
            // Two reasons:
            //   1. Rows that confirmed BEFORE this migration successfully
            //      dispatched their settlement webhook through the
            //      original monitor path — re-firing now would deliver a
            //      duplicate to the merchant.
            //   2. Historical stuck-state rows that lost the race + were
            //      recovered via operator SQL on the storefront side
            //      (see staging 2026-06-15: 3 Grin orders flipped to
            //      captured manually). For those, the Medusa payment
            //      session is already in 'captured' state — re-firing
            //      the webhook now would either be a duplicate-capture
            //      or a re-attempt on an order that has already shipped.
            // GrinInvoiceStatus.Confirmed = 3 (Pending=0,
            // AwaitingResponse=1, Broadcast=2, Confirmed=3, Expired=4).
            // Operators who genuinely need to re-dispatch a specific
            // historical row can manually UPDATE the column back to
            // FALSE; the monitor's RetryUnsignaledSettlements tick
            // will pick it up on the next 30s cycle.
            migrationBuilder.Sql(@"
                UPDATE ""BTCPayServer.Plugins.Grin"".""GrinInvoices""
                   SET ""SettlementWebhookSent"" = TRUE
                 WHERE ""Status"" = 3
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettlementWebhookSent",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices");
        }
    }
}
