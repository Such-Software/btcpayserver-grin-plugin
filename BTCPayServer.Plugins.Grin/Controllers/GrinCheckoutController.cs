using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using BTCPayServer.Rating;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Controllers;

[Route("stores/{storeId}/plugins/grin")]
public class GrinCheckoutController : Controller
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly GrinRateProvider _rateProvider;
    private readonly ILogger<GrinCheckoutController> _logger;

    public GrinCheckoutController(GrinService grinService, GrinRPCProvider rpcProvider,
        GrinRateProvider rateProvider, ILogger<GrinCheckoutController> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _rateProvider = rateProvider;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new Grin payment invoice. Called by the store's e-commerce integration.
    /// POST /stores/{storeId}/plugins/grin/invoices
    /// Body: { "amount": 1.5, "orderId": "order-123", "redirectUrl": "https://..." }
    /// </summary>
    [HttpPost("invoices")]
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
}

public class CreateGrinInvoiceRequest
{
    public decimal Amount { get; set; }
    public string OrderId { get; set; }
    public string RedirectUrl { get; set; }
    public string SessionId { get; set; }
    public string Currency { get; set; }
}
