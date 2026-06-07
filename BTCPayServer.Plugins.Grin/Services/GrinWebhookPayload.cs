using BTCPayServer.Plugins.Grin.Data;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Single source of truth for the webhook JSON payload, shared by both dispatch
/// paths — <see cref="GrinService.DispatchWebhook"/> (checkout / broadcast →
/// InvoiceProcessing) and <see cref="GrinPaymentMonitorService"/> (settled /
/// invalid / expired). Previously each path built its own anonymous object and
/// they had drifted: the monitor omitted the top-level <c>invoiceId</c>/
/// <c>storeId</c> and <c>confirmations</c>, and used <c>metadata.medusa_cart_id</c>
/// while the checkout path used <c>metadata.order_id</c>. Consumers that handle
/// both broadcast and settlement events therefore had to special-case two
/// shapes (and any keyed only on <c>order_id</c> silently missed settlements).
///
/// To stay backward compatible, the order reference is emitted under BOTH
/// <c>order_id</c> and <c>medusa_cart_id</c> (same value).
/// </summary>
public static class GrinWebhookPayload
{
    public static object Build(GrinInvoice invoice, string eventType) => new
    {
        @event = eventType,
        invoiceId = invoice.Id,
        storeId = invoice.StoreId,
        invoice = new
        {
            id = invoice.Id,
            status = invoice.Status.ToString(),
            amount = invoice.AmountNanogrin / 1_000_000_000m,
            confirmations = invoice.Confirmations,
            metadata = new
            {
                session_id = invoice.SessionId ?? "",
                order_id = invoice.OrderId ?? "",
                medusa_cart_id = invoice.OrderId ?? "",
            }
        }
    };
}
