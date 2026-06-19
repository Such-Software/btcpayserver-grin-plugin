using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Events;
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
    private readonly EventAggregator _eventAggregator;
    private readonly ILogger<GrinSettlementDispatcher> _logger;

    public GrinSettlementDispatcher(
        GrinWebhookDeliveryService deliveryService,
        PaymentService paymentService,
        InvoiceRepository invoiceRepository,
        PaymentMethodHandlerDictionary handlers,
        EventAggregator eventAggregator,
        ILogger<GrinSettlementDispatcher> logger)
    {
        _deliveryService = deliveryService;
        _paymentService = paymentService;
        _invoiceRepository = invoiceRepository;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _logger = logger;
    }

    /// <summary>
    /// Register the customer's payment with BTCPay AS SOON AS we
    /// broadcast the on-chain transaction (status -> Broadcast),
    /// not waiting for 10 confirmations to roll in. Two reasons:
    ///
    ///   1. <b>BTCPay invoice state</b>: without an AddPayment call,
    ///      the BTCPay invoice keeps ticking against its 15-minute
    ///      payment window and flips to Expired before our settlement
    ///      bridge ever fires. With a Processing-status payment
    ///      registered, BTCPay moves the invoice to Processing — the
    ///      canonical "paid, awaiting confirmations" state — same as
    ///      BTC / LN behave.
    ///   2. <b>Invoice list icon</b>: the Invoices list renders the
    ///      payment-method logo by iterating <c>invoice.Payments</c>.
    ///      Empty Payments => no logo. Registering on Broadcast means
    ///      the Grin logo shows up the moment the customer pays
    ///      instead of only after 10 confirmations.
    ///
    /// On Confirmed we re-call AddPayment with Status=Settled and let
    /// BTCPay's PaymentService.UpdatePayments (via the SetStatus
    /// extension) flip the existing record. PaymentData.Id is keyed
    /// on tx_slate_id so the second call hits the same row.
    /// </summary>
    public async Task DispatchSettlement(GrinInvoice invoice, GrinStoreSettings settings)
    {
        if (!string.IsNullOrEmpty(invoice.BtcpayInvoiceId))
        {
            await DispatchToBtcpay(invoice, PaymentStatus.Settled);
        }
        else
        {
            await _deliveryService.EnqueueDelivery(invoice, settings, "InvoicePaymentSettled");
        }
    }

    /// <summary>
    /// Called on Broadcast (slatepack finalized, tx posted to the
    /// network) BEFORE on-chain confirmations land. Registers a
    /// PaymentStatus.Processing payment with BTCPay so the invoice
    /// transitions out of the Expired-by-default countdown window
    /// and the Grin logo appears on the Invoices list immediately.
    /// </summary>
    public async Task DispatchBroadcast(GrinInvoice invoice, GrinStoreSettings settings)
    {
        if (!string.IsNullOrEmpty(invoice.BtcpayInvoiceId))
        {
            try
            {
                await DispatchToBtcpay(invoice, PaymentStatus.Processing);
            }
            catch (Exception ex)
            {
                // Broadcast-time AddPayment is best-effort. If BTCPay
                // is briefly unavailable here we don't want to fail
                // the broadcast itself — confirmation-time dispatch
                // will retry. Log + swallow.
                _logger.LogWarning(ex,
                    "DispatchBroadcast soft-failed for Grin invoice {InvoiceId} (BTCPay invoice {BtcpayInvoiceId}); will retry on confirmation",
                    invoice.Id, invoice.BtcpayInvoiceId);
            }
        }
        else
        {
            await _deliveryService.EnqueueDelivery(invoice, settings, "InvoiceProcessing");
        }
    }

    private async Task DispatchToBtcpay(GrinInvoice invoice, PaymentStatus status)
    {
        var pmi = GrinPaymentMethodConstants.PaymentMethodId;
        if (!_handlers.TryGetValue(pmi, out var handler))
        {
            _logger.LogError(
                "Grin payment handler not registered in PaymentMethodHandlerDictionary; cannot dispatch {Status} for BTCPay invoice {InvoiceId}",
                status, invoice.BtcpayInvoiceId);
            throw new InvalidOperationException("Grin payment handler missing from DI");
        }

        var invoiceEntity = await _invoiceRepository.GetInvoice(invoice.BtcpayInvoiceId);
        if (invoiceEntity == null)
        {
            _logger.LogWarning(
                "BTCPay invoice {InvoiceId} not found while dispatching {Status} for Grin invoice {GrinInvoiceId}",
                invoice.BtcpayInvoiceId, status, invoice.Id);
            return;
        }

        var amountGrin = invoice.AmountNanogrin / 1_000_000_000m;
        var paymentData = new PaymentData
        {
            Id = invoice.TxSlateId ?? invoice.Id,
            Created = invoice.PaidAt ?? DateTimeOffset.UtcNow,
            Status = status,
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
            // (Id, PaymentMethodId) pair already exists. For the
            // Broadcast→Confirmed promotion that's the EXPECTED case
            // on the second call: we already registered a Processing
            // payment at Broadcast, now we want to flip it to
            // Settled. The existing PaymentEntity (already on the
            // invoice in BTCPay's DB) needs its Status mutated
            // in place — re-constructing a fresh PaymentEntity to
            // pass to UpdatePayments would lose blob fields like
            // PaidAmount / NetworkFee, so we re-read the live row
            // off the invoice, set Status, and write back.
            if (status == PaymentStatus.Settled)
            {
                var liveInvoice = await _invoiceRepository.GetInvoice(invoice.BtcpayInvoiceId);
                var existing = liveInvoice?.GetPayments(false)?
                    .FirstOrDefault(p =>
                        p.Id == (invoice.TxSlateId ?? invoice.Id) &&
                        p.PaymentMethodId == pmi);
                if (existing != null && existing.Status != PaymentStatus.Settled)
                {
                    existing.Status = PaymentStatus.Settled;
                    await _paymentService.UpdatePayments(new List<PaymentEntity> { existing });
                    _logger.LogInformation(
                        "BTCPay payment for Grin invoice {InvoiceId} promoted to Settled (tx_slate_id={TxSlateId})",
                        invoice.BtcpayInvoiceId, invoice.TxSlateId);
                }
                else
                {
                    _logger.LogInformation(
                        "BTCPay payment for Grin invoice {InvoiceId} already Settled or row missing — treating as success",
                        invoice.BtcpayInvoiceId);
                }
            }
            else
            {
                _logger.LogInformation(
                    "BTCPay payment for Grin invoice {InvoiceId} was already at status {Status}; treating as success",
                    invoice.BtcpayInvoiceId, status);
            }
            // Even on the "already exists" path, nudge InvoiceWatcher
            // to re-evaluate the invoice's state. Two-fold purpose:
            //   1. Recovers invoices stuck at "New" from pre-1.3.2
            //      installations where the AddPayment-on-Broadcast
            //      call didn't publish ReceivedPayment.
            //   2. Safety net for any case where the InvoiceWatcher's
            //      scheduled Wait() tick fired between AddPayment
            //      registering the payment and BTCPay's recompute
            //      window seeing it — the watcher dequeues the
            //      invoice on a single Wait() pass; without re-Watch
            //      events the invoice would never re-evaluate.
            _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.BtcpayInvoiceId));
        }
        else
        {
            // Publish ReceivedPayment so BTCPay's InvoiceWatcher
            // recomputes the parent invoice's state — without this
            // the AddPayment row exists in the DB but the invoice
            // stays at "New" forever (matches Bitcoin/Lightning
            // pattern at LightningListener.cs:666 and
            // NBXplorerListener.cs:185). InvoiceWatcher reads
            // payment.Accounted (Processing or Settled) and
            // promotes the invoice to Processing → Settled
            // accordingly. Without the event, no recompute fires.
            _eventAggregator.Publish(
                new InvoiceEvent(invoiceEntity, InvoiceEvent.ReceivedPayment)
                {
                    Payment = payment,
                });
            _logger.LogInformation(
                "BTCPay payment recorded for Grin invoice {InvoiceId} status={Status} tx_slate_id={TxSlateId} amount={Amount} GRIN",
                invoice.BtcpayInvoiceId, status, invoice.TxSlateId, amountGrin);
        }
    }
}
