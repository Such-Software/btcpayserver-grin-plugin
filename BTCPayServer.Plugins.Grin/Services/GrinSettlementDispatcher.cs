using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Single funnel for "this Grin invoice just reached MinConfirmations."
/// Both the customer-side /status poll endpoint
/// (<c>GrinCheckoutController.Status</c>) and the background
/// <see cref="GrinPaymentMonitorService"/> reach this point on a
/// freshly-confirmed invoice; having one dispatcher means the
/// settlement logic stays in lockstep across both paths.
///
/// Routing is by <see cref="GrinInvoice.BtcpayInvoiceId"/>:
///   - Non-null: invoice was created through BTCPay's first-class
///     invoice flow (<see cref="GrinPaymentMethodHandler"/>). We hand
///     a <see cref="PaymentData"/> to BTCPay's
///     <see cref="PaymentService"/>; BTCPay's event aggregator
///     publishes <c>InvoiceEvent.PaymentSettled</c> and BTCPay's
///     built-in WebhookSender delivers the canonical
///     <c>InvoicePaymentSettled</c> event to every URL the operator
///     has registered in the store's Webhooks settings. Same code
///     path Bitcoin / Lightning / official altcoin plugins use.
///   - Null: invoice was created via the in-plugin direct route
///     (legacy, retained for backward compatibility on staging during
///     the transition window). Falls back to the plugin's own
///     webhook delivery queue.
///
/// Throws on dispatch failure so the caller can revert the
/// <see cref="GrinInvoice.SettlementWebhookSent"/> guard and let
/// the next retry cycle take another shot.
/// </summary>
public class GrinSettlementDispatcher
{
    private readonly GrinWebhookDeliveryService _deliveryService;
    private readonly PaymentService _paymentService;
    private readonly InvoiceRepository _invoiceRepository;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<GrinSettlementDispatcher> _logger;

    public GrinSettlementDispatcher(
        GrinWebhookDeliveryService deliveryService,
        PaymentService paymentService,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        ILogger<GrinSettlementDispatcher> logger)
    {
        _deliveryService = deliveryService;
        _paymentService = paymentService;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _logger = logger;
    }

    public async Task DispatchSettlement(GrinInvoice invoice, GrinStoreSettings settings)
    {
        if (!string.IsNullOrEmpty(invoice.BtcpayInvoiceId))
        {
            await DispatchToBtcpay(invoice);
        }
        else
        {
            await _deliveryService.EnqueueDelivery(invoice, settings, "InvoicePaymentSettled");
        }
    }

    private async Task DispatchToBtcpay(GrinInvoice invoice)
    {
        var pmi = GrinPaymentMethodConstants.PaymentMethodId;
        if (!_handlers.TryGetValue(pmi, out var handler))
        {
            // Shouldn't reach here — Plugin.cs registers the handler
            // at boot. If it does, throw so the caller's revert path
            // kicks in and we surface the misconfig in logs.
            _logger.LogError(
                "Grin payment handler not registered in PaymentMethodHandlerDictionary; cannot dispatch settlement for BTCPay invoice {InvoiceId}",
                invoice.BtcpayInvoiceId);
            throw new InvalidOperationException("Grin payment handler missing from DI");
        }

        var invoiceEntity = await _invoiceRepository.GetInvoice(invoice.BtcpayInvoiceId);
        if (invoiceEntity == null)
        {
            // BTCPay invoice deleted between issuance and settlement.
            // Not retriable — the row is gone. Log + return; the
            // caller's guard stays set so we don't churn.
            _logger.LogWarning(
                "BTCPay invoice {InvoiceId} not found while dispatching settlement for Grin invoice {GrinInvoiceId}",
                invoice.BtcpayInvoiceId, invoice.Id);
            return;
        }

        var amountGrin = invoice.AmountNanogrin / 1_000_000_000m;
        var paymentData = new PaymentData
        {
            Id = invoice.TxSlateId ?? invoice.Id,
            Created = invoice.PaidAt ?? DateTimeOffset.UtcNow,
            Status = PaymentStatus.Settled,
            Currency = GrinPaymentMethodConstants.CryptoCode,
            InvoiceDataId = invoice.BtcpayInvoiceId,
            Amount = amountGrin,
        }.Set(invoiceEntity, handler, new GrinPaymentPromptDetails
        {
            TxSlateId = invoice.TxSlateId,
            SlatepackAddress = invoice.SlatepackAddress,
            SlatepackMessage = invoice.IssuedSlatepack,
            GrinInvoiceId = invoice.Id,
            AmountNanogrin = invoice.AmountNanogrin,
        });

        var payment = await _paymentService.AddPayment(paymentData,
            new HashSet<string> { invoice.TxSlateId ?? invoice.Id });
        if (payment == null)
        {
            // PaymentService.AddPayment returns null when the
            // (Id, PaymentMethodId) pair already exists — idempotent
            // success. The confirmation already landed via a prior
            // tick or the customer-side /status poll.
            _logger.LogInformation(
                "BTCPay payment for Grin invoice {InvoiceId} was already recorded; treating as success",
                invoice.BtcpayInvoiceId);
        }
        else
        {
            _logger.LogInformation(
                "BTCPay payment recorded for Grin invoice {InvoiceId} (tx_slate_id={TxSlateId}, amount={Amount} GRIN)",
                invoice.BtcpayInvoiceId, invoice.TxSlateId, amountGrin);
        }
    }
}
