using System;
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
    private readonly ILogger<GrinPaymentMonitorService> _logger;
    private Timer _timer;

    // Invoices older than this with no response are expired
    private static readonly TimeSpan InvoiceExpiry = TimeSpan.FromHours(24);
    // How often to run the monitor loop
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public GrinPaymentMonitorService(GrinService grinService, GrinRPCProvider rpcProvider,
        ILogger<GrinPaymentMonitorService> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
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
                    if (!tx.TryGetProperty("kernel_lookup_min_height", out var minHeight) ||
                        minHeight.ValueKind == JsonValueKind.Null)
                        continue;

                    var heightResult = await client.NodeHeight();
                    if (!heightResult.TryGetProperty("Ok", out var heightOk))
                        continue;

                    var currentHeight = GetIntFromJson(heightOk, "height");
                    var txHeight = minHeight.ValueKind == JsonValueKind.String
                        ? int.Parse(minHeight.GetString()!)
                        : minHeight.GetInt32();

                    if (currentHeight <= 0 || txHeight <= 0)
                        continue;

                    var confirmations = currentHeight - txHeight + 1;

                    if (confirmations >= settings.MinConfirmations)
                    {
                        await _grinService.UpdateInvoiceStatus(invoice.Id,
                            GrinInvoiceStatus.Confirmed, confirmations);
                        _logger.LogInformation(
                            "Invoice {InvoiceId} confirmed with {Confirmations} confirmations",
                            invoice.Id, confirmations);
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

                // Cancel the tx in the wallet so funds aren't locked
                var settings = await _grinService.GetStoreSettings(invoice.StoreId);
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

    private static int GetIntFromJson(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var val))
            return 0;
        return val.ValueKind == JsonValueKind.String
            ? int.Parse(val.GetString()!)
            : val.GetInt32();
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
