using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Grin.Migrations
{
    /// <summary>
    /// Adds the cross-reference column linking a <c>GrinInvoice</c> to
    /// the BTCPay <c>InvoiceData</c> row it was created for (when the
    /// invoice was created through BTCPay's first-class payment flow
    /// via <c>GrinPaymentMethodHandler</c>). Null for invoices created
    /// via the legacy direct route — those are tied to a Medusa cart,
    /// not a BTCPay invoice.
    ///
    /// The column is nullable so existing rows stay valid without a
    /// backfill, and so the legacy / new code paths can coexist
    /// during the gradual migration to the BTCPay-native flow.
    /// </summary>
    [DbContext(typeof(GrinDbContext))]
    [Migration("20260615180000_AddBtcpayInvoiceId")]
    public partial class AddBtcpayInvoiceId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BtcpayInvoiceId",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices",
                type: "text",
                nullable: true);

            // Lookup index — the Phase B payment-bridge in the monitor
            // will need to walk from a BTCPay invoice id to our row
            // when it dispatches PaymentService.AddPayment. Partial
            // index keeps it tiny since legacy rows have NULL here.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_GrinInvoices_BtcpayInvoiceId""
                    ON ""BTCPayServer.Plugins.Grin"".""GrinInvoices"" (""BtcpayInvoiceId"")
                 WHERE ""BtcpayInvoiceId"" IS NOT NULL
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""BTCPayServer.Plugins.Grin"".""IX_GrinInvoices_BtcpayInvoiceId""");
            migrationBuilder.DropColumn(
                name: "BtcpayInvoiceId",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinInvoices");
        }
    }
}
