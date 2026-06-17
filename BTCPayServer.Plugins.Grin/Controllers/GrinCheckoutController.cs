using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using BTCPayServer.Rating;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Controllers;

[Route("stores/{storeId}/plugins/grin")]
public class GrinCheckoutController : Controller
{
    private readonly GrinService _grinService;
    private readonly GrinWebhookDeliveryService _deliveryService;
    private readonly GrinSettlementDispatcher _settlementDispatcher;
    private readonly GrinRPCProvider _rpcProvider;
    // Cached + health-tracked rate provider. NEVER inject GrinRateProvider
    // here directly — that bypasses caching and pays a synchronous
    // Gate.io HTTP call on every invoice creation. See Plugin.cs DI chain.
    private readonly GrinRateHealth _rateProvider;
    private readonly ILogger<GrinCheckoutController> _logger;

    public GrinCheckoutController(GrinService grinService,
        GrinWebhookDeliveryService deliveryService,
        GrinSettlementDispatcher settlementDispatcher,
        GrinRPCProvider rpcProvider,
        GrinRateHealth rateProvider, ILogger<GrinCheckoutController> logger)
    {
        _grinService = grinService;
        _deliveryService = deliveryService;
        _settlementDispatcher = settlementDispatcher;
        _rpcProvider = rpcProvider;
        _rateProvider = rateProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new Grin payment invoice. Called by the store's e-commerce integration.
    /// POST /stores/{storeId}/plugins/grin/invoices
    /// Body: { "amount": 1.5, "orderId": "order-123", "redirectUrl": "https://..." }
    ///
    /// Authentication: BTCPay Greenfield API key with `btcpay.store.cancreateinvoice`
    /// scope, passed as `Authorization: token &lt;api-key&gt;`. Matches the convention
    /// of every other invoice-creating endpoint in BTCPay
    /// (see GreenfieldInvoiceController.CreateInvoice). Operator provisions
    /// the key per-store in BTCPay → Settings → Account → API Keys and the
    /// integration (e.g. Medusa) pastes it into its provider config.
    /// </summary>
    // External API endpoint — called from e-commerce integrations
    // (Medusa, custom storefronts) without a BTCPay session/cookie.
    // BTCPay 2.3.9 added a global UIControllerAntiforgeryTokenAttribute
    // filter that rejects every cookie-less POST with an empty-body 400
    // before the action runs. Selectively opt this one action out;
    // the rest of the controller (slatepack-submit form etc.) still
    // gets antiforgery via the global filter where it makes sense.
    [HttpPost("invoices")]
    [Authorize(Policy = Policies.CanCreateInvoice,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    [Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
    public async Task<IActionResult> CreateInvoice(string storeId, [FromBody] CreateGrinInvoiceRequest request)
    {
        var settings = await _grinService.GetStoreSettings(storeId);
        if (settings == null || !settings.Enabled)
            return BadRequest(new { error = "Grin payments not configured for this store." });

        if (request.Amount <= 0)
            return BadRequest(new { error = "Amount must be greater than zero." });

        try
        {
            var client = await _rpcProvider.GetClient(settings);

            // Convert fiat to GRIN if a non-GRIN currency is specified.
            // The GrinRateProvider fetches GRIN/USDT from Gate.io.
            var grinAmount = request.Amount;
            if (!string.IsNullOrEmpty(request.Currency)
                && !request.Currency.Equals("GRIN", StringComparison.OrdinalIgnoreCase))
            {
                var rates = await _rateProvider.GetRatesAsync(CancellationToken.None);
                var pair = rates.FirstOrDefault(r =>
                    r.CurrencyPair.Right.Equals(request.Currency, StringComparison.OrdinalIgnoreCase));
                if (pair == null || pair.BidAsk.Ask <= 0)
                    return BadRequest(new { error = $"No GRIN/{request.Currency} rate available." });

                // Amount is in fiat, divide by GRIN price to get GRIN amount.
                // Round up to nearest whole GRIN to avoid undercharging.
                grinAmount = Math.Ceiling(request.Amount / pair.BidAsk.Ask);
                _logger.LogInformation(
                    "Converted {FiatAmount} {Currency} → {GrinAmount} GRIN at rate {Rate}",
                    request.Amount, request.Currency, grinAmount, pair.BidAsk.Ask);
            }

            // Convert GRIN to nanogrin (1 GRIN = 1,000,000,000 nanogrin)
            var amountNanogrin = (long)(grinAmount * 1_000_000_000m);

            // Issue invoice via grin-wallet
            var invoiceResult = await client.IssueInvoiceTx(amountNanogrin,
                $"Payment for order {request.OrderId}");

            if (!invoiceResult.TryGetProperty("Ok", out var slate))
            {
                _logger.LogError("IssueInvoiceTx failed: {Response}", invoiceResult);
                return StatusCode(500, new { error = "Failed to create Grin invoice." });
            }

            // Extract tx_slate_id from the slate
            var txSlateId = slate.GetProperty("id").GetString();

            // Get slatepack address for Tor-based payments
            var addressResult = await client.GetSlatepackAddress();
            string slatepackAddress = null;
            if (addressResult.TryGetProperty("Ok", out var addrOk))
                slatepackAddress = addrOk.GetString();

            // Encode the slate as a slatepack message for manual copy/paste
            var slatepackResult = await client.CreateSlatepackMessage(slate);
            string slatepackMessage = null;
            if (slatepackResult.TryGetProperty("Ok", out var spOk))
                slatepackMessage = spOk.GetString();

            // Generate a unique invoice ID
            var invoiceId = Guid.NewGuid().ToString("N")[..12];

            // Save to database
            var grinInvoice = await _grinService.CreateInvoice(
                invoiceId, storeId, amountNanogrin,
                slatepackAddress, slatepackMessage, txSlateId,
                request.SessionId, request.OrderId, request.RedirectUrl);

            _logger.LogInformation("Grin invoice {InvoiceId} created: {Amount} GRIN, tx_slate_id={TxSlateId}",
                invoiceId, grinAmount, txSlateId);

            var checkoutUrl = Url.Action(nameof(Checkout), "GrinCheckout",
                new { storeId, invoiceId }, Request.Scheme);

            return Ok(new
            {
                invoiceId,
                checkoutUrl,
                amount = grinAmount,
                amountNanogrin,
                txSlateId,
                slatepackAddress,
                status = grinInvoice.Status.ToString()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Grin invoice for store {StoreId}", storeId);
            return StatusCode(500, new { error = $"Failed to create invoice: {ex.Message}" });
        }
    }

    /// <summary>
    /// Fetch full Grin invoice details (slatepack address, message, amount,
    /// status). JSON. Used by external storefronts that render their own
    /// checkout UI (e.g. Medusa's /grin-pay route) instead of redirecting
    /// the customer to the plugin-hosted Checkout view.
    ///
    /// Auth: same Greenfield API key required to create the invoice; we
    /// reuse CanCreateInvoice rather than introducing a new scope (a
    /// caller able to create the invoice in the first place is, by
    /// definition, entitled to read its own state back).
    ///
    /// GET /stores/{storeId}/plugins/grin/invoices/{invoiceId}
    /// </summary>
    [HttpGet("invoices/{invoiceId}")]
    [Authorize(Policy = Policies.CanCreateInvoice,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> GetInvoice(string storeId, string invoiceId)
    {
        var invoice = await _grinService.GetInvoice(invoiceId);
        if (invoice == null || invoice.StoreId != storeId)
            return NotFound();

        return Ok(new
        {
            invoiceId = invoice.Id,
            storeId = invoice.StoreId,
            status = invoice.Status.ToString(),
            amountNanogrin = invoice.AmountNanogrin,
            amountGrin = invoice.AmountNanogrin / 1_000_000_000m,
            slatepackAddress = invoice.SlatepackAddress,
            issuedSlatepack = invoice.IssuedSlatepack,
            txSlateId = invoice.TxSlateId,
            confirmations = invoice.Confirmations,
            orderId = invoice.OrderId,
            redirectUrl = invoice.RedirectUrl,
            btcpayInvoiceId = invoice.BtcpayInvoiceId,
            createdAt = invoice.CreatedAt,
            paidAt = invoice.PaidAt,
        });
    }

    /// <summary>
    /// Checkout page — shows slatepack address (Tor) and manual slatepack flow.
    /// GET /stores/{storeId}/plugins/grin/checkout/{invoiceId}
    /// </summary>
    [HttpGet("checkout/{invoiceId}")]
    public async Task<IActionResult> Checkout(string storeId, string invoiceId)
    {
        var invoice = await _grinService.GetInvoice(invoiceId);
        if (invoice == null || invoice.StoreId != storeId)
            return NotFound();

        if (invoice.Status == GrinInvoiceStatus.Confirmed)
            return View("CheckoutComplete", invoice);

        if (invoice.Status == GrinInvoiceStatus.Expired)
            return View("CheckoutExpired", invoice);

        // Auto-redirect on Broadcast (payment detected, awaiting confirmations)
        // when redirectUrl is set. The order is already created in Medusa via
        // the InvoiceProcessing webhook dispatched in SubmitSlatepack.
        //
        // Open-redirect guard: validate the stored RedirectUrl before
        // bouncing the customer there. RedirectUrl is supplied by the
        // e-commerce integration at invoice-creation time and stored
        // verbatim. Without validation, an attacker who could call
        // CreateInvoice with a hostile RedirectUrl (or who later
        // mutates the row via DB access) could 302 customers to a
        // phishing page under this trusted BTCPay hostname.
        //
        // Acceptable forms: absolute https:// URLs with a host whose
        // scheme + structure is well-formed. Rejected:
        //   - javascript:, data:, file:, mailto:, tel: (script
        //     injection or out-of-band action)
        //   - URLs with embedded credentials (user:pass@host — the
        //     classic open-redirect tactic that confuses humans about
        //     the actual host)
        //   - http:// in production (downgrade attack on customers
        //     coming from a TLS-secured BTCPay page)
        //
        // Full per-store allowlist is TODO P9 — what's here is the
        // pragmatic version that blocks the obvious attacks without
        // requiring per-tenant config.
        if (invoice.Status == GrinInvoiceStatus.Broadcast
            && !string.IsNullOrEmpty(invoice.RedirectUrl)
            && IsSafeRedirect(invoice.RedirectUrl))
        {
            return Redirect(invoice.RedirectUrl);
        }

        var settings = await _grinService.GetStoreSettings(storeId);
        ViewBag.MinConfirmations = settings?.MinConfirmations ?? 10;
        ViewBag.GrinUsdPrice = await _grinService.GetGrinUsdPrice();
        return View("Checkout", invoice);
    }

    /// <summary>
    /// Accept customer's response slatepack (manual flow).
    /// POST /stores/{storeId}/plugins/grin/checkout/{invoiceId}/submit
    /// </summary>
    [HttpPost("checkout/{invoiceId}/submit")]
    public async Task<IActionResult> SubmitSlatepack(string storeId, string invoiceId,
        [FromForm] string responseSlatepack)
    {
        var invoice = await _grinService.GetInvoice(invoiceId);
        if (invoice == null || invoice.StoreId != storeId)
            return NotFound();

        if (invoice.Status == GrinInvoiceStatus.Broadcast || invoice.Status == GrinInvoiceStatus.Confirmed)
        {
            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }

        if (invoice.Status == GrinInvoiceStatus.Expired)
        {
            TempData["Error"] = "This invoice has expired. Please create a new one.";
            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }

        if (string.IsNullOrWhiteSpace(responseSlatepack))
        {
            TempData["Error"] = "Please paste the response slatepack from your wallet.";
            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }

        var settings = await _grinService.GetStoreSettings(storeId);
        if (settings == null)
            return StatusCode(500);

        try
        {
            var client = await _rpcProvider.GetClient(settings);

            // Step 1: Decode the customer's response slatepack
            JsonElement decodedSlate;
            try
            {
                var decodeResult = await client.DecodeSlatepack(responseSlatepack.Trim());
                if (!decodeResult.TryGetProperty("Ok", out decodedSlate))
                {
                    _logger.LogWarning("DecodeSlatepack returned error: {Response}", decodeResult);
                    TempData["Error"] = "Invalid slatepack. Please check that you pasted the complete response from your wallet.";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 1 (decode) OK. Slate type: {Type}, raw: {Raw}",
                    decodedSlate.ValueKind, decodedSlate.ToString()[..Math.Min(200, decodedSlate.ToString().Length)]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DecodeSlatepack exception");
                TempData["Error"] = "Failed to decode slatepack. Please check the format and try again.";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            // Step 2: Finalize the transaction
            JsonElement finalizedSlate;
            try
            {
                var finalizeResult = await client.FinalizeTx(decodedSlate);
                if (!finalizeResult.TryGetProperty("Ok", out finalizedSlate))
                {
                    _logger.LogWarning("FinalizeTx returned error: {Response}", finalizeResult);
                    TempData["Error"] = "Could not finalize the transaction. The slatepack may be for a different invoice or already used.";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 2 (finalize) OK. Slate type: {Type}, raw: {Raw}",
                    finalizedSlate.ValueKind, finalizedSlate.ToString()[..Math.Min(200, finalizedSlate.ToString().Length)]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FinalizeTx exception");
                TempData["Error"] = "Failed to finalize the transaction. Please try again.";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            // Step 3: Post (broadcast) the transaction
            try
            {
                var postResult = await client.PostTx(finalizedSlate, fluff: true);
                if (!postResult.TryGetProperty("Ok", out _))
                {
                    _logger.LogWarning("PostTx returned error: {Response}", postResult);
                    TempData["Error"] = "Failed to broadcast the transaction. Please try again or contact the merchant.";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 3 (broadcast) OK");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PostTx exception");
                TempData["Error"] = "Failed to broadcast the transaction. Please try again.";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            await _grinService.UpdateInvoiceStatus(invoiceId, GrinInvoiceStatus.Broadcast);
            _logger.LogInformation("Grin invoice {InvoiceId} finalized and broadcast", invoiceId);

            // Enqueue the "payment detected" broadcast webhook so Medusa
            // can transition the cart to "authorized" + create the order
            // record before chain confirmation arrives. Enqueueing
            // returns immediately; the worker
            // (GrinWebhookDeliveryWorker) picks it up within ~5s and
            // delivers with the same backoff retry policy that protects
            // the settlement webhook. Pre-2026-06-15 this was a
            // fire-and-forget HTTP POST that could lose the broadcast
            // event entirely on contention.
            try
            {
                var updatedInvoice = await _grinService.GetInvoice(invoiceId);
                if (updatedInvoice != null)
                {
                    await _deliveryService.EnqueueDelivery(updatedInvoice, settings, "InvoiceProcessing");
                }
            }
            catch (Exception enqEx)
            {
                _logger.LogWarning(enqEx, "Failed to enqueue broadcast webhook for invoice {InvoiceId}", invoiceId);
            }

            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process slatepack for invoice {InvoiceId}", invoiceId);
            TempData["Error"] = "An unexpected error occurred. Please try again or contact the merchant.";
            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }
    }

    /// <summary>
    /// Payment status polling endpoint (JSON).
    /// GET /stores/{storeId}/plugins/grin/checkout/{invoiceId}/status
    /// Actively checks wallet for confirmation updates when invoice is Broadcast.
    /// </summary>
    [HttpGet("checkout/{invoiceId}/status")]
    public async Task<IActionResult> Status(string storeId, string invoiceId)
    {
        var invoice = await _grinService.GetInvoice(invoiceId);
        if (invoice == null || invoice.StoreId != storeId)
            return NotFound();

        // If broadcast, actively check wallet for confirmation updates
        if (invoice.Status == GrinInvoiceStatus.Broadcast && !string.IsNullOrEmpty(invoice.TxSlateId))
        {
            try
            {
                var settings = await _grinService.GetStoreSettings(storeId);
                if (settings != null)
                {
                    var client = await _rpcProvider.GetClient(settings);

                    // Capture node height BEFORE retrieve_txs to avoid race condition
                    // where a block arrives between calls, inflating our count by 1
                    long currentHeight = await GetNodeHeight(client);

                    var txResult = await client.RetrieveTxs(invoice.TxSlateId);

                    if (txResult.TryGetProperty("Ok", out var okResult))
                    {
                        // Result is [refreshed, [tx1, tx2, ...]]
                        JsonElement txList;
                        if (okResult.ValueKind == JsonValueKind.Array && okResult.GetArrayLength() >= 2)
                            txList = okResult[1];
                        else
                            txList = okResult;

                        if (txList.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tx in txList.EnumerateArray())
                            {
                                var isConfirmed = tx.TryGetProperty("confirmed", out var c) && c.GetBoolean();
                                if (!isConfirmed)
                                    continue;

                                var confirmations = await GetConfirmationsFromOutputs(client, tx, currentHeight);

                                if (confirmations >= settings.MinConfirmations)
                                {
                                    await _grinService.UpdateInvoiceStatus(invoiceId,
                                        GrinInvoiceStatus.Confirmed, confirmations);
                                    invoice.Status = GrinInvoiceStatus.Confirmed;
                                    invoice.Confirmations = confirmations;

                                    // Settlement-dispatch race: customer-side /status
                                    // poll fires every 5s, background monitor every
                                    // 30s. Both reach this branch on a freshly-confirmed
                                    // invoice. TryMarkSettlementWebhookSent is an atomic
                                    // UPDATE on a guard column — Postgres row-locks the
                                    // row at statement scope, so exactly one caller
                                    // gets rows-affected=1 and owns the dispatch.
                                    // Losers see false and skip silently.
                                    //
                                    // Once the guard is held, we enqueue a delivery row;
                                    // GrinWebhookDeliveryWorker takes it from there
                                    // (retries on its own exponential backoff schedule
                                    // up to ~32h before dead-lettering). The handler
                                    // is therefore at-most-once-per-confirmation but
                                    // at-least-once-delivered-from-the-queue, which is
                                    // exactly what we want.
                                    if (await _grinService.TryMarkSettlementWebhookSent(invoiceId))
                                    {
                                        try
                                        {
                                            await _settlementDispatcher.DispatchSettlement(
                                                invoice, settings);
                                        }
                                        catch (Exception dispatchEx)
                                        {
                                            _logger.LogError(dispatchEx,
                                                "Failed to dispatch InvoicePaymentSettled for invoice {InvoiceId} — reverting guard so the monitor can retry",
                                                invoiceId);
                                            await _grinService.ResetSettlementWebhookFlag(invoiceId);
                                        }
                                    }
                                }
                                else if (confirmations > invoice.Confirmations)
                                {
                                    await _grinService.UpdateInvoiceStatus(invoiceId,
                                        GrinInvoiceStatus.Broadcast, confirmations);
                                    invoice.Confirmations = confirmations;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check confirmations for invoice {InvoiceId}", invoiceId);
            }
        }

        return Ok(new
        {
            invoiceId = invoice.Id,
            status = invoice.Status.ToString(),
            amountGrin = invoice.AmountNanogrin / 1_000_000_000m,
            confirmations = invoice.Confirmations,
            paidAt = invoice.PaidAt
        });
    }

    /// <summary>
    /// Get node height from the wallet's RPC.
    /// </summary>
    private static async Task<long> GetNodeHeight(GrinRPCClient client)
    {
        var heightResult = await client.NodeHeight();
        if (!heightResult.TryGetProperty("Ok", out var heightOk))
            return 0;
        if (heightOk.TryGetProperty("height", out var h))
        {
            return h.ValueKind == JsonValueKind.String
                ? long.Parse(h.GetString()!)
                : h.GetInt64();
        }
        return 0;
    }

    /// <summary>
    /// Get actual confirmation count by looking up the output's mined height.
    /// TxLogEntry only has kernel_lookup_min_height (scan start), not the actual mined height.
    /// We use retrieve_outputs with the tx's local id to get OutputData.height.
    /// currentHeight must be captured BEFORE retrieve_txs to avoid race conditions.
    /// </summary>
    private static async Task<int> GetConfirmationsFromOutputs(GrinRPCClient client, JsonElement tx, long currentHeight)
    {
        if (currentHeight <= 0)
            return 0;

        // Get the tx's local id (not the slate id)
        if (!tx.TryGetProperty("id", out var idProp))
            return 1;
        var txId = idProp.ValueKind == JsonValueKind.String
            ? int.Parse(idProp.GetString()!)
            : idProp.GetInt32();

        // Get outputs for this tx
        var outputResult = await client.RetrieveOutputs(txId);
        if (!outputResult.TryGetProperty("Ok", out var okOutputs))
            return 1;

        // Result is [refreshed, [output1, output2, ...]]
        JsonElement outputList;
        if (okOutputs.ValueKind == JsonValueKind.Array && okOutputs.GetArrayLength() >= 2)
            outputList = okOutputs[1];
        else
            outputList = okOutputs;

        if (outputList.ValueKind != JsonValueKind.Array || outputList.GetArrayLength() == 0)
            return 1;

        // Get the highest output height (the block this tx was confirmed in)
        long outputHeight = 0;
        foreach (var output in outputList.EnumerateArray())
        {
            // OutputCommitMapping has { output: OutputData, commit: ... }
            var outputData = output.TryGetProperty("output", out var od) ? od : output;
            if (outputData.TryGetProperty("height", out var h))
            {
                var height = h.ValueKind == JsonValueKind.String
                    ? long.Parse(h.GetString()!)
                    : h.GetInt64();
                if (height > outputHeight)
                    outputHeight = height;
            }
        }

        if (outputHeight <= 0 || currentHeight < outputHeight)
            return 1;

        return (int)(currentHeight - outputHeight + 1);
    }

    /// <summary>
    /// Validate a stored RedirectUrl before bouncing the customer there.
    /// Permissive enough to accept any legitimate e-commerce return URL
    /// (https + well-formed) and strict enough to block the obvious
    /// open-redirect tactics (embedded creds, script schemes, http
    /// downgrade). Per-store allowlist is TODO P9.
    /// </summary>
    private static bool IsSafeRedirect(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Must be a parseable absolute URI.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;

        // Only https; http downgrade attack on a customer arriving from
        // BTCPay's TLS-secured checkout is unacceptable.
        if (parsed.Scheme != Uri.UriSchemeHttps) return false;

        // No `user:pass@host` form — classic open-redirect obfuscation.
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;

        // Host must be non-empty and not a loopback / link-local — those
        // would let an attacker pivot the customer's browser onto our
        // internal network surfaces. Real storefronts run on public
        // hosts.
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        if (parsed.IsLoopback) return false;

        return true;
    }
}

public class CreateGrinInvoiceRequest
{
    public decimal Amount { get; set; }
    public string OrderId { get; set; }
    public string RedirectUrl { get; set; }
    public string SessionId { get; set; }
    public string Currency { get; set; }
}
