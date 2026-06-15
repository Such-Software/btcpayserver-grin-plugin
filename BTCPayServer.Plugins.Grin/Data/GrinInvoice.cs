using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Grin.Data;

public class GrinInvoice
{
    [Key]
    public string Id { get; set; }                  // BTCPay invoice ID

    public string StoreId { get; set; }             // BTCPay store ID
    public string TxSlateId { get; set; }           // Grin tx UUID
    public string SlatepackAddress { get; set; }    // Per-invoice slatepack address
    public string IssuedSlatepack { get; set; }     // Slatepack message shown to customer
    public long AmountNanogrin { get; set; }
    public GrinInvoiceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public int Confirmations { get; set; }
    public string SessionId { get; set; }       // Medusa payment session ID
    public string OrderId { get; set; }          // Medusa cart/order reference
    public string RedirectUrl { get; set; }      // Post-payment redirect URL

    /// <summary>
    /// If non-null, this <see cref="GrinInvoice"/> was created by the
    /// BTCPay first-class payment flow (<c>GrinPaymentMethodHandler</c>)
    /// and corresponds to a row in BTCPay's own <c>Invoices</c> table.
    /// When the monitor confirms this invoice on-chain, the Phase B
    /// bridge will call <c>PaymentService.AddPayment</c> with this id
    /// to flip the BTCPay invoice to paid.
    ///
    /// Null for invoices created via the legacy direct route
    /// (<c>POST /stores/{id}/plugins/grin/invoices</c>) — those are
    /// Medusa-bound and use our own webhook queue instead of BTCPay's
    /// event machinery.
    /// </summary>
    public string BtcpayInvoiceId { get; set; }

    /// <summary>
    /// True once an <c>InvoicePaymentSettled</c> webhook has been
    /// successfully dispatched for this invoice's current
    /// confirmation. Acts as the cross-process / cross-call atomic
    /// guard so the customer-side /status poll and the background
    /// <c>GrinPaymentMonitorService</c> can't both fire the settlement
    /// webhook on the same confirmation event.
    ///
    /// Reset to <c>false</c> when the invoice transitions back to
    /// <c>Broadcast</c> via a reorg detection so the re-confirmation
    /// fires a fresh notification.
    /// </summary>
    public bool SettlementWebhookSent { get; set; }
}

public enum GrinInvoiceStatus
{
    Pending,
    AwaitingResponse,
    Broadcast,
    Confirmed,
    Expired
}
