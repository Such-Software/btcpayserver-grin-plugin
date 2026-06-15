namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// What we store in the <c>PaymentPrompt.Details</c> JSON blob for a
/// Grin payment. Survives a round-trip through BTCPay's invoice
/// serialization (<see cref="GrinPaymentMethodHandler.Serializer"/>),
/// so add fields only if they're stable across invoice replays.
///
/// <c>SlatepackMessage</c> is the merchant-side "send this to me"
/// payload shown to the customer in the checkout view. <c>TxSlateId</c>
/// is the UUID linking back to a row in <c>GrinInvoices</c> + the
/// grin-wallet's own transaction store; the monitor uses it to poll
/// for confirmations.
///
/// <c>GrinInvoiceId</c> is the foreign key into our own
/// <c>GrinInvoices</c> table (mirror invoice id; not the BTCPay
/// invoice id). Lets the handler / monitor walk back to our
/// row without a join through tx_slate_id.
/// </summary>
public class GrinPaymentPromptDetails
{
    public string TxSlateId { get; set; }
    public string SlatepackMessage { get; set; }
    public string SlatepackAddress { get; set; }
    public string GrinInvoiceId { get; set; }
    public long AmountNanogrin { get; set; }
}
