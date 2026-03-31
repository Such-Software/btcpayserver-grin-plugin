using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinPaymentMonitorService : IHostedService, IDisposable
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrinPaymentMonitorService> _logger;
    private Timer _timer;

    // Invoices older than this with no response are expired
    private static readonly TimeSpan InvoiceExpiry = TimeSpan.FromHours(24);
    // How often to run the monitor loop
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public GrinPaymentMonitorService(GrinService grinService, GrinRPCProvider rpcProvider,
        IHttpClientFactory httpClientFactory, ILogger<GrinPaymentMonitorService> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Grin payment monitor started");
        _timer = new Timer(DoWork, null, TimeSpan.FromSeconds(10), PollInterval);
        return Task.CompletedTask;
    }

    private async void DoWork(object state)
    {
        try
        {
            await CheckBroadcastInvoices();
            await ExpireStaleInvoices();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grin payment monitor error");
        }
    }

    private async Task CheckBroadcastInvoices()
    {
        var pending = await _grinService.GetPendingInvoices();

        foreach (var invoice in pending)
        {
            if (invoice.Status != GrinInvoiceStatus.Broadcast)
                continue;

            if (string.IsNullOrEmpty(invoice.TxSlateId))
                continue;

            try
            {
                var settings = await _grinService.GetStoreSettings(invoice.StoreId);
                if (settings == null || !settings.Enabled)
                    continue;

                var client = await _rpcProvider.GetClient(settings);

                // Capture height BEFORE retrieve_txs to avoid race condition
                long currentHeight = await GetNodeHeight(client);

                var txResult = await client.RetrieveTxs(invoice.TxSlateId);

                if (!txResult.TryGetProperty("Ok", out var okResult))
                    continue;

                JsonElement txList;
                if (okResult.ValueKind == JsonValueKind.Array && okResult.GetArrayLength() >= 2)
                    txList = okResult[1];
                else
                    txList = okResult;

                if (txList.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var tx in txList.EnumerateArray())
                {
                    var isConfirmed = tx.TryGetProperty("confirmed", out var conf) && conf.GetBoolean();
                    if (!isConfirmed)
                        continue;

                    var confirmations = await GetConfirmationsFromOutputs(client, tx, currentHeight);

                    if (confirmations >= settings.MinConfirmations)
                    {
                        await _grinService.UpdateInvoiceStatus(invoice.Id,
                            GrinInvoiceStatus.Confirmed, confirmations);
                        _logger.LogInformation(
                            "Invoice {InvoiceId} confirmed with {Confirmations} confirmations",
                            invoice.Id, confirmations);

                        // Dispatch webhook to Medusa so order gets created
                        await DispatchWebhookAsync(invoice, settings, "InvoicePaymentSettled");
                    }
                    else if (confirmations > invoice.Confirmations)
                    {
                        await _grinService.UpdateInvoiceStatus(invoice.Id,
                            GrinInvoiceStatus.Broadcast, confirmations);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check invoice {InvoiceId}", invoice.Id);
            }
        }
    }

    private async Task ExpireStaleInvoices()
    {
        var stale = await _grinService.GetExpiredCandidates(InvoiceExpiry);

        foreach (var invoice in stale)
        {
            try
            {
                await _grinService.UpdateInvoiceStatus(invoice.Id, GrinInvoiceStatus.Expired);
                _logger.LogInformation("Expired stale invoice {InvoiceId} (created {CreatedAt})",
                    invoice.Id, invoice.CreatedAt);

                // Dispatch expiry webhook
                var settings = await _grinService.GetStoreSettings(invoice.StoreId);
                if (settings != null)
                    await DispatchWebhookAsync(invoice, settings, "InvoiceExpired");

                // Cancel the tx in the wallet so funds aren't locked
                if (settings != null && !string.IsNullOrEmpty(invoice.TxSlateId))
                {
                    try
                    {
                        var client = await _rpcProvider.GetClient(settings);
                        await client.CancelTx(invoice.TxSlateId);
                        _logger.LogInformation("Cancelled wallet tx {TxSlateId} for expired invoice {InvoiceId}",
                            invoice.TxSlateId, invoice.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cancel wallet tx for expired invoice {InvoiceId}",
                            invoice.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to expire invoice {InvoiceId}", invoice.Id);
            }
        }
    }

    /// <summary>
    /// Send webhook to Medusa backend so payment status is updated and order is created.
    /// Uses the same BTCPay-compat event format that our crypto checkout providers expect.
    /// </summary>
    private async Task DispatchWebhookAsync(GrinInvoice invoice, GrinStoreSettings settings, string eventType)
    {
        if (string.IsNullOrEmpty(settings.WebhookUrl))
            return;

        try
        {
            var amountGrin = invoice.AmountNanogrin / 1_000_000_000m;
            var payload = new
            {
                @event = eventType,
                invoice = new
                {
                    id = invoice.Id,
                    status = invoice.Status.ToString(),
                    amount = amountGrin,
                    metadata = new
                    {
                        session_id = invoice.SessionId ?? "",
                        medusa_cart_id = invoice.OrderId ?? "",
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // HMAC-SHA256 signature (same format as xmrcheckout/wowcheckout)
            if (!string.IsNullOrEmpty(settings.WebhookSecret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.WebhookSecret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(json));
                var sig = "sha256=" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                content.Headers.Add("btcpay-sig", sig);
            }

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var response = await client.PostAsync(settings.WebhookUrl, content);

            _logger.LogInformation(
                "Webhook dispatched for invoice {InvoiceId}: {EventType} → {Url} (status {StatusCode})",
                invoice.Id, eventType, settings.WebhookUrl, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch webhook for invoice {InvoiceId}", invoice.Id);
        }
    }

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
    /// Get actual confirmation count from output height.
    /// currentHeight must be captured BEFORE retrieve_txs to avoid race conditions.
    /// </summary>
    private static async Task<int> GetConfirmationsFromOutputs(GrinRPCClient client, JsonElement tx, long currentHeight)
    {
        if (currentHeight <= 0)
            return 0;

        if (!tx.TryGetProperty("id", out var idProp))
            return 1;
        var txId = idProp.ValueKind == JsonValueKind.String
            ? int.Parse(idProp.GetString()!)
            : idProp.GetInt32();

        var outputResult = await client.RetrieveOutputs(txId);
        if (!outputResult.TryGetProperty("Ok", out var okOutputs))
            return 1;

        JsonElement outputList;
        if (okOutputs.ValueKind == JsonValueKind.Array && okOutputs.GetArrayLength() >= 2)
            outputList = okOutputs[1];
        else
            outputList = okOutputs;

        if (outputList.ValueKind != JsonValueKind.Array || outputList.GetArrayLength() == 0)
            return 1;

        long outputHeight = 0;
        foreach (var output in outputList.EnumerateArray())
        {
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Grin payment monitor stopped");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
