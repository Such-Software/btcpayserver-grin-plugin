using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinService
{
    private readonly GrinDbContextFactory _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrinService> _logger;

    // Cached GRIN/USD price
    private decimal _cachedGrinUsd;
    private DateTimeOffset _cacheExpiry;

    public GrinService(GrinDbContextFactory dbContextFactory, IHttpClientFactory httpClientFactory,
        ILogger<GrinService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get GRIN/USD price from Gate.io, cached for 2 minutes.
    /// </summary>
    public async Task<decimal> GetGrinUsdPrice()
    {
        if (_cachedGrinUsd > 0 && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cachedGrinUsd;

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetStringAsync(
                "https://api.gateio.ws/api/v4/spot/tickers?currency_pair=GRIN_USDT");
            var arr = JArray.Parse(response);
            var last = arr[0]?["last"]?.Value<decimal>() ?? 0m;
            if (last > 0)
            {
                _cachedGrinUsd = last;
                _cacheExpiry = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            return _cachedGrinUsd;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch GRIN/USD price from Gate.io");
            return _cachedGrinUsd; // return stale cache if available
        }
    }

    // Store settings CRUD

    public async Task<GrinStoreSettings> GetStoreSettings(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinStoreSettings.FindAsync(storeId);
    }

    public async Task SaveStoreSettings(GrinStoreSettings settings)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.GrinStoreSettings.FindAsync(settings.StoreId);
        if (existing == null)
        {
            await ctx.GrinStoreSettings.AddAsync(settings);
        }
        else
        {
            existing.OwnerApiUrl = settings.OwnerApiUrl;
            existing.WalletPassword = settings.WalletPassword;
            existing.ApiSecret = settings.ApiSecret;
            existing.NodeApiUrl = settings.NodeApiUrl;
            existing.MinConfirmations = settings.MinConfirmations;
            existing.Enabled = settings.Enabled;
            existing.WebhookUrl = settings.WebhookUrl;
            existing.WebhookSecret = settings.WebhookSecret;
        }
        await ctx.SaveChangesAsync();
    }

    public async Task<List<GrinStoreSettings>> GetAllEnabledStores()
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinStoreSettings
            .Where(s => s.Enabled)
            .ToListAsync();
    }

    // Invoice CRUD

    public async Task<GrinInvoice> CreateInvoice(string invoiceId, string storeId,
        long amountNanogrin, string slatepackAddress, string issuedSlatepack, string txSlateId,
        string sessionId = null, string orderId = null, string redirectUrl = null)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var invoice = new GrinInvoice
        {
            Id = invoiceId,
            StoreId = storeId,
            AmountNanogrin = amountNanogrin,
            SlatepackAddress = slatepackAddress,
            IssuedSlatepack = issuedSlatepack,
            TxSlateId = txSlateId,
            SessionId = sessionId,
            OrderId = orderId,
            RedirectUrl = redirectUrl,
            Status = GrinInvoiceStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await ctx.GrinInvoices.AddAsync(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }

    public async Task<GrinInvoice> GetInvoice(string invoiceId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices.FindAsync(invoiceId);
    }

    public async Task<List<GrinInvoice>> GetPendingInvoices()
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices
            .Where(i => i.Status != GrinInvoiceStatus.Confirmed &&
                        i.Status != GrinInvoiceStatus.Expired)
            .ToListAsync();
    }

    public async Task<List<GrinInvoice>> GetInvoicesByStore(string storeId, int limit = 50)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices
            .Where(i => i.StoreId == storeId)
            .OrderByDescending(i => i.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<GrinInvoice>> GetExpiredCandidates(TimeSpan maxAge)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        return await ctx.GrinInvoices
            .Where(i => (i.Status == GrinInvoiceStatus.Pending || i.Status == GrinInvoiceStatus.AwaitingResponse)
                        && i.CreatedAt < cutoff)
            .ToListAsync();
    }

    /// <summary>
    /// Recently-confirmed invoices that the monitor should keep
    /// double-checking for reorgs. Grin reorgs deeper than ~10 blocks
    /// are essentially unheard of (network has 30s+ block time and
    /// solid hashpower), but the window is configurable so operators
    /// can extend it if they ship high-value items.
    ///
    /// The cutoff is by <c>PaidAt</c> (when the transition to
    /// Confirmed happened), not <c>CreatedAt</c>, so an invoice
    /// confirmed today still gets monitored regardless of when the
    /// customer first opened the checkout page.
    /// </summary>
    public async Task<List<GrinInvoice>> GetReorgMonitoringCandidates(TimeSpan window)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var cutoff = DateTimeOffset.UtcNow - window;
        return await ctx.GrinInvoices
            .Where(i => i.Status == GrinInvoiceStatus.Confirmed
                        && i.PaidAt != null
                        && i.PaidAt > cutoff)
            .ToListAsync();
    }

    public async Task UpdateInvoiceStatus(string invoiceId, GrinInvoiceStatus status, int confirmations = 0)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var invoice = await ctx.GrinInvoices.FindAsync(invoiceId);
        if (invoice == null) return;

        invoice.Status = status;
        invoice.Confirmations = confirmations;
        if (status == GrinInvoiceStatus.Confirmed)
            invoice.PaidAt = DateTimeOffset.UtcNow;
        // Reset the settlement-dispatch guard whenever the invoice
        // transitions BACK to Broadcast (reorg). The fresh
        // confirmation that eventually follows must fire a new
        // notification. For non-reorg paths this is a no-op: a
        // freshly-created invoice already has SettlementWebhookSent=
        // false.
        if (status == GrinInvoiceStatus.Broadcast)
            invoice.SettlementWebhookSent = false;

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Atomically claim the right to dispatch the
    /// <c>InvoicePaymentSettled</c> webhook for an invoice. Returns
    /// <c>true</c> exactly once across all concurrent callers; subsequent
    /// (or losing) callers get <c>false</c>.
    ///
    /// Implementation is a single UPDATE...WHERE...AND
    /// SettlementWebhookSent=false. PostgreSQL row-locks the row at
    /// statement scope, so even when the Status endpoint (5s poll) and
    /// the monitor service (30s loop) land in the same millisecond,
    /// only one's affected-row count is 1.
    /// </summary>
    public async Task<bool> TryMarkSettlementWebhookSent(string invoiceId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var rows = await ctx.GrinInvoices
            .Where(i => i.Id == invoiceId && !i.SettlementWebhookSent)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(i => i.SettlementWebhookSent, true));
        return rows == 1;
    }

    /// <summary>
    /// Revert the SettlementWebhookSent flag so subsequent calls
    /// (or the monitor's retry-unsignaled loop) can attempt dispatch
    /// again. Used when a TryMark succeeded but the actual webhook
    /// dispatch returned non-2xx and we want the chance to retry.
    /// Becomes obsolete once B3 (persistent webhook delivery queue +
    /// retry worker) lands; for now this is the simplest safety net.
    /// </summary>
    public async Task ResetSettlementWebhookFlag(string invoiceId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.GrinInvoices
            .Where(i => i.Id == invoiceId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(i => i.SettlementWebhookSent, false));
    }

    /// <summary>
    /// Confirmed invoices whose settlement webhook has not yet been
    /// dispatched. The monitor's tick uses this to retry settlement
    /// notifications that lost a race or whose initial dispatch
    /// returned non-2xx and reverted the flag.
    /// </summary>
    public async Task<List<GrinInvoice>> GetUnsignaledSettlements()
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices
            .Where(i => i.Status == GrinInvoiceStatus.Confirmed
                        && !i.SettlementWebhookSent)
            .ToListAsync();
    }

    /// <summary>
    /// Dispatch a webhook for a Grin invoice status change.
    /// Same BTCPay-compat format as GrinPaymentMonitorService uses.
    /// Returns <c>true</c> on HTTP 2xx, <c>false</c> on network
    /// failure or non-2xx response. Returns <c>true</c> when the
    /// store has no <c>WebhookUrl</c> configured (nothing to deliver,
    /// equivalent to success from the caller's perspective so the
    /// settlement guard stays set).
    /// </summary>
    public async Task<bool> DispatchWebhook(GrinStoreSettings settings, GrinInvoice invoice, string eventType)
    {
        if (string.IsNullOrEmpty(settings.WebhookUrl))
            return true;

        try
        {
            var payload = new
            {
                @event = eventType,
                invoiceId = invoice.Id,
                storeId = invoice.StoreId,
                invoice = new
                {
                    id = invoice.Id,
                    status = invoice.Status.ToString(),
                    amount = invoice.AmountNanogrin / 1_000_000_000m,
                    confirmations = invoice.Confirmations,
                    metadata = new
                    {
                        session_id = invoice.SessionId ?? "",
                        order_id = invoice.OrderId ?? "",
                    },
                },
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var body = System.Text.Encoding.UTF8.GetBytes(json);

            // HMAC-SHA256 signature — shared helper keeps this path and
            // GrinPaymentMonitorService.DispatchWebhookAsync in lockstep.
            // Helper returns "" when no secret configured; in that case
            // we skip the header entirely rather than emit a bogus
            // "sha256=" with no value (which Medusa would reject as
            // malformed).
            var sig = WebhookSignature.Compute(settings.WebhookSecret, body);

            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl)
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (!string.IsNullOrEmpty(sig))
            {
                request.Headers.Add("btcpay-sig", sig);
            }
            request.Headers.Add("User-Agent", "btcpayserver-grin-plugin/1.0");

            var response = await httpClient.SendAsync(request);
            _logger.LogInformation(
                "Webhook dispatched for invoice {InvoiceId}: {EventType} → {StatusCode}",
                invoice.Id, eventType, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch webhook for invoice {InvoiceId}", invoice.Id);
            return false;
        }
    }
}
