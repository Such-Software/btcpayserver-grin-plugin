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

    public async Task UpdateInvoiceStatus(string invoiceId, GrinInvoiceStatus status, int confirmations = 0)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var invoice = await ctx.GrinInvoices.FindAsync(invoiceId);
        if (invoice == null) return;

        invoice.Status = status;
        invoice.Confirmations = confirmations;
        if (status == GrinInvoiceStatus.Confirmed)
            invoice.PaidAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Dispatch a webhook for a Grin invoice status change.
    /// Same BTCPay-compat format as GrinPaymentMonitorService uses.
    /// </summary>
    public async Task DispatchWebhook(GrinStoreSettings settings, GrinInvoice invoice, string eventType)
    {
        if (string.IsNullOrEmpty(settings.WebhookUrl))
            return;

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

            // HMAC-SHA256 signature
            string signature = "";
            if (!string.IsNullOrEmpty(settings.WebhookSecret))
            {
                using var hmac = new System.Security.Cryptography.HMACSHA256(
                    System.Text.Encoding.UTF8.GetBytes(settings.WebhookSecret));
                signature = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
            }

            var httpClient = _httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl)
            {
                Content = new ByteArrayContent(body)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.Add("btcpay-sig", $"sha256={signature}");
            request.Headers.Add("User-Agent", "btcpayserver-grin-plugin/1.0");

            var response = await httpClient.SendAsync(request);
            _logger.LogInformation(
                "Webhook dispatched for invoice {InvoiceId}: {EventType} → {StatusCode}",
                invoice.Id, eventType, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch webhook for invoice {InvoiceId}", invoice.Id);
        }
    }
}
