using System;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Controllers;

[Route("stores/{storeId}/plugins/grin")]
public class GrinCheckoutController : Controller
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly ILogger<GrinCheckoutController> _logger;

    public GrinCheckoutController(GrinService grinService, GrinRPCProvider rpcProvider,
        ILogger<GrinCheckoutController> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
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

            // Convert GRIN to nanogrin (1 GRIN = 1,000,000,000 nanogrin)
            var amountNanogrin = (long)(request.Amount * 1_000_000_000m);

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
                slatepackAddress, slatepackMessage, txSlateId);

            _logger.LogInformation("Grin invoice {InvoiceId} created: {Amount} GRIN, tx_slate_id={TxSlateId}",
                invoiceId, request.Amount, txSlateId);

            var checkoutUrl = Url.Action(nameof(Checkout), "GrinCheckout",
                new { storeId, invoiceId }, Request.Scheme);

            return Ok(new
            {
                invoiceId,
                checkoutUrl,
                amount = request.Amount,
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
                    TempData["Error"] = $"Invalid slatepack: {decodeResult}";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 1 (decode) OK. Slate type: {Type}, raw: {Raw}",
                    decodedSlate.ValueKind, decodedSlate.ToString()[..Math.Min(200, decodedSlate.ToString().Length)]);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Decode failed: {ex.Message}";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            // Step 2: Finalize the transaction
            JsonElement finalizedSlate;
            try
            {
                var finalizeResult = await client.FinalizeTx(decodedSlate);
                if (!finalizeResult.TryGetProperty("Ok", out finalizedSlate))
                {
                    TempData["Error"] = $"Finalize failed: {finalizeResult}";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 2 (finalize) OK. Slate type: {Type}, raw: {Raw}",
                    finalizedSlate.ValueKind, finalizedSlate.ToString()[..Math.Min(200, finalizedSlate.ToString().Length)]);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Finalize failed: {ex.Message}";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            // Step 3: Post (broadcast) the transaction
            try
            {
                var postResult = await client.PostTx(finalizedSlate, fluff: true);
                if (!postResult.TryGetProperty("Ok", out _))
                {
                    TempData["Error"] = $"Broadcast failed: {postResult}";
                    return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
                }
                _logger.LogInformation("Step 3 (broadcast) OK");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Broadcast failed: {ex.Message}";
                return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
            }

            await _grinService.UpdateInvoiceStatus(invoiceId, GrinInvoiceStatus.Broadcast);
            _logger.LogInformation("Grin invoice {InvoiceId} finalized and broadcast", invoiceId);

            return RedirectToAction(nameof(Checkout), new { storeId, invoiceId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process slatepack for invoice {InvoiceId}", invoiceId);
            TempData["Error"] = $"Unexpected error: {ex.GetType().Name}: {ex.Message}";
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
                                var confirmed = tx.TryGetProperty("confirmed", out var c) && c.GetBoolean();
                                var confirmations = 0;

                                if (tx.TryGetProperty("kernel_lookup_min_height", out var minHeight) &&
                                    minHeight.ValueKind != JsonValueKind.Null)
                                {
                                    // Get current node height to calculate confirmations
                                    var heightResult = await client.NodeHeight();
                                    if (heightResult.TryGetProperty("Ok", out var heightOk))
                                    {
                                        var currentHeight = heightOk.TryGetProperty("height", out var h)
                                            ? (h.ValueKind == JsonValueKind.String
                                                ? (int)ulong.Parse(h.GetString())
                                                : (int)h.GetUInt64())
                                            : 0;
                                        var txHeight = minHeight.ValueKind == JsonValueKind.String
                                            ? (int)ulong.Parse(minHeight.GetString())
                                            : (int)minHeight.GetUInt64();
                                        if (currentHeight > 0 && txHeight > 0)
                                            confirmations = currentHeight - txHeight + 1;
                                    }
                                }

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
}

public class CreateGrinInvoiceRequest
{
    public decimal Amount { get; set; }
    public string OrderId { get; set; }
    public string RedirectUrl { get; set; }
}
