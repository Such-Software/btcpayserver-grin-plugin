using System;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Grin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// Bridges the Grin plugin into BTCPay's first-class payment-method
/// system. With this handler registered, Grin invoices created via
/// BTCPay's own invoice flow (e.g. operator clicks "Create Invoice"
/// in the BTCPay UI, or Greenfield <c>POST /api/v1/stores/{id}/invoices</c>)
/// land in the standard <c>InvoiceData</c> table alongside Bitcoin /
/// Lightning invoices, with Grin showing up as a selectable payment
/// method on the store's settings page.
///
/// <b>Phase A scope (this commit):</b> the handler is fully implemented
/// and registered in DI, so BTCPay treats Grin as a known payment
/// method. <see cref="ConfigurePrompt"/> calls the existing
/// <see cref="GrinService"/> to issue a real wallet transaction and
/// stamps the result onto the <c>PaymentPrompt</c>. Invoices created
/// this way appear in BTCPay's invoice list, in the operator's
/// transaction history, and on the store's payment-method settings
/// page.
///
/// <b>Phase B (deferred — operator decision required):</b> when an
/// invoice created via the BTCPay flow confirms on-chain, the
/// monitor doesn't yet call BTCPay's <c>PaymentService.AddPayment</c>
/// — so the invoice will stay "waiting for payment" in BTCPay's UI
/// even after we mark it Confirmed in our own <c>GrinInvoices</c>
/// table. Plugging this bridge requires:
///   1. Identifying which <c>GrinInvoices</c> rows correspond to
///      BTCPay-flow invoices vs the legacy direct-call flow used by
///      Medusa today (we already stamp <c>GrinInvoice.BtcpayInvoiceId</c>
///      from this handler — see the migration shipped alongside).
///   2. Constructing a <c>PaymentData</c> on confirm and calling
///      <c>PaymentService.AddPayment</c> from
///      <c>GrinPaymentMonitorService</c>.
///   3. Deciding whether to also delete / deprecate the parallel
///      <c>GrinInvoices</c> + webhook queue once the BTCPay path is
///      proven — that's a cross-system decision (Medusa integration
///      consumes our webhook format today).
///
/// <b>Existing direct-route flow is untouched.</b>
/// <c>GrinCheckoutController.CreateInvoice</c> (Medusa's integration
/// surface) continues to write to <c>GrinInvoices</c> with
/// <c>BtcpayInvoiceId = null</c> and dispatches through the
/// <c>GrinWebhookDelivery</c> queue (B3). Operators upgrade from the
/// legacy flow to the BTCPay-native flow independently.
/// </summary>
public class GrinPaymentMethodHandler : IPaymentMethodHandler
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly GrinRateHealth _rateHealth;

    public PaymentMethodId PaymentMethodId => GrinPaymentMethodConstants.PaymentMethodId;

    /// <summary>
    /// BlobSerializer's no-network overload gives us the
    /// camelCase + null-skipping settings BTCPay's invoice blobs
    /// expect. Don't substitute <c>JsonConvert.SerializeObject</c> —
    /// it'll silently emit PascalCase + nulls and BTCPay's Greenfield
    /// API consumers will get a payload that doesn't match the
    /// (camelCase) schema.
    /// </summary>
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public GrinPaymentMethodHandler(
        GrinService grinService,
        GrinRPCProvider rpcProvider,
        GrinRateHealth rateHealth)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _rateHealth = rateHealth;
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = GrinPaymentMethodConstants.CryptoCode;
        context.Prompt.Divisibility = GrinPaymentMethodConstants.Divisibility;
        context.Prompt.PaymentMethodFee = 0m;
        // Grin has no on-chain fee charged to the customer in our
        // model — invoice tx is paid by the merchant's wallet on
        // post_tx. If we ever offer "customer pays fees" we'd
        // surface it here.
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        // Surface the live wallet config as the source of truth.
        // BTCPay's PaymentMethodConfig blob is just a "Enabled?" hint
        // — credentials + URL stay in our own settings table.
        var storeId = context.Store.Id;
        var settings = await _grinService.GetStoreSettings(storeId);
        if (settings == null || !settings.Enabled)
        {
            throw new PaymentMethodUnavailableException(
                "Grin is not configured (or is disabled) for this store. " +
                "Configure the wallet at /stores/{id}/plugins/grin.");
        }

        var due = context.Prompt.Calculate().Due;
        // PaymentPrompt amounts are in the base currency (GRIN). Round UP
        // to TWO decimal places of GRIN before converting to nanogrin so
        // customers can type a sane amount into their wallet (e.g. 41.07
        // instead of 41.069352964). With GRIN/USD around $0.025, the
        // largest rounding overcharge is ~$0.00025 — well within the
        // noise of crypto rate movement and orders of magnitude smaller
        // than what's saved by not making customers type nine decimals.
        var grinAmount = Math.Ceiling(due * 100m) / 100m;
        var amountNanogrin = (long)(grinAmount * 1_000_000_000m);

        var client = await _rpcProvider.GetClient(settings);
        var invoiceResult = await client.IssueInvoiceTx(amountNanogrin,
            $"Payment for invoice {context.InvoiceEntity.Id}");
        if (!invoiceResult.TryGetProperty("Ok", out var slate))
        {
            throw new PaymentMethodUnavailableException(
                $"grin-wallet IssueInvoiceTx failed: {invoiceResult}");
        }

        var txSlateId = slate.GetProperty("id").GetString();
        var addressResult = await client.GetSlatepackAddress();
        string slatepackAddress = null;
        if (addressResult.TryGetProperty("Ok", out var addrOk))
            slatepackAddress = addrOk.GetString();
        var slatepackResult = await client.CreateSlatepackMessage(slate);
        string slatepackMessage = null;
        if (slatepackResult.TryGetProperty("Ok", out var spOk))
            slatepackMessage = spOk.GetString();

        // Mirror to our own GrinInvoices table so the monitor /
        // existing tooling can find the row. Stamp BtcpayInvoiceId so
        // the Phase B payment-bridge knows this row corresponds to a
        // BTCPay-flow invoice (vs the legacy direct-call flow used by
        // Medusa, where BtcpayInvoiceId is null).
        var grinInvoiceId = Guid.NewGuid().ToString("N")[..12];
        await _grinService.CreateInvoice(
            grinInvoiceId, storeId, amountNanogrin,
            slatepackAddress, slatepackMessage, txSlateId,
            sessionId: null,
            orderId: context.InvoiceEntity.Id,
            redirectUrl: null,
            btcpayInvoiceId: context.InvoiceEntity.Id);

        // Populate the BTCPay-side prompt. Destination is the
        // customer-visible address; Details is the slatepack message
        // they actually copy/paste back to us.
        context.Prompt.Destination = slatepackAddress;
        context.Prompt.Details = JToken.FromObject(new GrinPaymentPromptDetails
        {
            TxSlateId = txSlateId,
            SlatepackMessage = slatepackMessage,
            SlatepackAddress = slatepackAddress,
            GrinInvoiceId = grinInvoiceId,
            AmountNanogrin = amountNanogrin,
        }, Serializer);
        // DO NOT add slatepackAddress to TrackedDestinations: every Grin
        // invoice issued by the same wallet has the SAME slatepack
        // address (the merchant's static address, not a fresh per-invoice
        // address like BTC). BTCPay's AddressInvoice table is PK'd on
        // the destination string, so adding it would succeed for the
        // first invoice and throw a unique-constraint violation
        // (PK_AddressInvoices) on every subsequent invoice — blocking
        // all Grin invoice creation via Greenfield with a 500.
        // We don't need address-based invoice resolution anyway:
        // GrinPaymentMonitorService tracks confirmations by txSlateId
        // and routes via GrinInvoice.BtcpayInvoiceId. txSlateId IS
        // unique per invoice, so we still surface it as a search term
        // so operators can find the BTCPay invoice from a tx_slate_id.
        context.AdditionalSearchTerms.Add(txSlateId);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
        => details.ToObject<GrinPaymentPromptDetails>(Serializer);

    public GrinPaymentPromptDetails ParsePaymentPromptDetails(JToken details)
        => details.ToObject<GrinPaymentPromptDetails>(Serializer);

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
        => ParsePaymentMethodConfig(config);

    public GrinPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        => config?.ToObject<GrinPaymentMethodConfig>(Serializer) ?? new GrinPaymentMethodConfig();

    public object ParsePaymentDetails(JToken details)
        => details.ToObject<GrinPaymentPromptDetails>(Serializer);
}
