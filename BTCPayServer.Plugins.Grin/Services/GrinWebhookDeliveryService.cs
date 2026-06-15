using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Owns the persistent webhook delivery queue + retry policy.
/// Callers (Status endpoint, monitor) enqueue a delivery with
/// <see cref="EnqueueDelivery"/>. The worker
/// (<see cref="GrinWebhookDeliveryWorker"/>) walks ready rows and
/// hands each one to <see cref="GrinService.DispatchWebhook"/>, then
/// flips status via <see cref="MarkDelivered"/> or
/// <see cref="MarkFailed"/>.
///
/// Centralizing the retry policy in one place means tuning the
/// backoff schedule doesn't ripple through every dispatch site —
/// edit <see cref="RetrySchedule"/> and every code path inherits.
/// </summary>
public class GrinWebhookDeliveryService
{
    private readonly GrinDbContextFactory _dbContextFactory;
    private readonly ILogger<GrinWebhookDeliveryService> _logger;

    /// <summary>
    /// Backoff schedule for the <c>AttemptCount</c>-th retry. Returns
    /// the wait time AFTER an attempt fails. Index 0 = wait before the
    /// 1st retry (so 30s after the initial Pending dispatch fails).
    /// Total wall-clock window from first failure to DeadLetter:
    /// 30s + 2m + 10m + 1h + 6h + 24h ≈ 31h. Indices past the end of
    /// this array transition the row to <c>DeadLetter</c>.
    /// </summary>
    private static readonly TimeSpan[] RetrySchedule = new[]
    {
        TimeSpan.FromSeconds(30),  // 0 → 1
        TimeSpan.FromMinutes(2),   // 1 → 2
        TimeSpan.FromMinutes(10),  // 2 → 3
        TimeSpan.FromHours(1),     // 3 → 4
        TimeSpan.FromHours(6),     // 4 → 5
        TimeSpan.FromHours(24),    // 5 → 6 (final)
    };

    /// <summary>
    /// One initial attempt + <see cref="RetrySchedule"/>.Length retries.
    /// </summary>
    public static int MaxAttempts => RetrySchedule.Length + 1;

    public GrinWebhookDeliveryService(GrinDbContextFactory dbContextFactory,
        ILogger<GrinWebhookDeliveryService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Build the JSON payload for a webhook delivery and enqueue a
    /// Pending row so the worker can dispatch it. Returns the
    /// enqueued delivery's id, or <c>null</c> if no <c>WebhookUrl</c>
    /// is configured (nothing to deliver).
    ///
    /// <c>EventType</c> + <c>InvoiceId</c> together define a logical
    /// settlement event. The caller is responsible for deduping —
    /// the queue does NOT enforce a unique constraint on those
    /// columns because legitimate use cases (a settlement → reorg →
    /// re-settlement) require multiple rows for the same pair. The
    /// atomic guard on <c>GrinInvoice.SettlementWebhookSent</c>
    /// handles the settle-side dedup; <c>EnqueueDelivery</c> here is
    /// dumb-pipe.
    /// </summary>
    public async Task<string> EnqueueDelivery(GrinInvoice invoice, GrinStoreSettings settings, string eventType)
    {
        if (string.IsNullOrEmpty(settings.WebhookUrl))
            return null;

        var amountGrin = invoice.AmountNanogrin / 1_000_000_000m;
        var payload = JsonSerializer.Serialize(new
        {
            @event = eventType,
            invoiceId = invoice.Id,
            storeId = invoice.StoreId,
            invoice = new
            {
                id = invoice.Id,
                status = invoice.Status.ToString(),
                amount = amountGrin,
                confirmations = invoice.Confirmations,
                metadata = new
                {
                    session_id = invoice.SessionId ?? "",
                    order_id = invoice.OrderId ?? "",
                },
            },
        });

        var now = DateTimeOffset.UtcNow;
        var delivery = new GrinWebhookDelivery
        {
            Id = Guid.NewGuid().ToString("N"),
            InvoiceId = invoice.Id,
            StoreId = invoice.StoreId,
            EventType = eventType,
            Url = settings.WebhookUrl,
            Payload = payload,
            Status = GrinWebhookDeliveryStatus.Pending,
            AttemptCount = 0,
            NextAttemptAt = now, // ready immediately
            CreatedAt = now,
        };

        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.GrinWebhookDeliveries.AddAsync(delivery);
        await ctx.SaveChangesAsync();

        _logger.LogInformation(
            "Webhook enqueued: delivery {DeliveryId} for invoice {InvoiceId} event {EventType}",
            delivery.Id, invoice.Id, eventType);

        return delivery.Id;
    }

    /// <summary>
    /// Rows ready to attempt RIGHT NOW: status in (Pending, Failed)
    /// AND <c>NextAttemptAt</c> &lt;= now. Caller iterates and
    /// dispatches; results from each dispatch feed back through
    /// <see cref="MarkDelivered"/> / <see cref="MarkFailed"/>.
    ///
    /// Limit caps the batch size per tick so a sudden flood doesn't
    /// monopolize the worker. 100 is generous — settlement traffic
    /// is at the human-purchase tempo, not load-test.
    /// </summary>
    public async Task<List<GrinWebhookDelivery>> GetReadyDeliveries(int limit = 100)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinWebhookDeliveries
            .Where(d => (d.Status == GrinWebhookDeliveryStatus.Pending
                         || d.Status == GrinWebhookDeliveryStatus.Failed)
                        && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Final state: the merchant returned 2xx. Stamp the response
    /// code so audit history shows the actual landing, not "magic
    /// success".
    /// </summary>
    public async Task MarkDelivered(string deliveryId, int responseCode)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.GrinWebhookDeliveries
            .Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, GrinWebhookDeliveryStatus.Delivered)
                .SetProperty(d => d.AttemptCount, d => d.AttemptCount + 1)
                .SetProperty(d => d.LastAttemptAt, now)
                .SetProperty(d => d.LastResponseCode, (int?)responseCode)
                .SetProperty(d => d.LastError, (string)null)
                .SetProperty(d => d.DeliveredAt, (DateTimeOffset?)now));
    }

    /// <summary>
    /// Failure path. Bumps attempt count, schedules the next
    /// <c>NextAttemptAt</c> from the backoff table, stamps the error
    /// + response code. If the attempt count exhausts the retry
    /// schedule, transitions to <c>DeadLetter</c> instead.
    ///
    /// <c>responseCode</c> is null for transport-level failures
    /// (DNS, connection refused, timeout) — caller passes whatever
    /// it has.
    /// </summary>
    public async Task MarkFailed(string deliveryId, int? responseCode, string error)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = _dbContextFactory.CreateContext();
        var delivery = await ctx.GrinWebhookDeliveries.FindAsync(deliveryId);
        if (delivery == null) return;

        var nextAttempt = delivery.AttemptCount; // 0-indexed into RetrySchedule for the next wait
        delivery.AttemptCount += 1;
        delivery.LastAttemptAt = now;
        delivery.LastResponseCode = responseCode;
        delivery.LastError = Truncate(error, 2000);

        if (nextAttempt >= RetrySchedule.Length)
        {
            delivery.Status = GrinWebhookDeliveryStatus.DeadLetter;
            _logger.LogError(
                "Webhook delivery {DeliveryId} DEAD-LETTERED after {AttemptCount} attempts. Invoice {InvoiceId} event {EventType} last code={Code} last error={Error}",
                deliveryId, delivery.AttemptCount, delivery.InvoiceId, delivery.EventType,
                responseCode, delivery.LastError);
        }
        else
        {
            delivery.Status = GrinWebhookDeliveryStatus.Failed;
            delivery.NextAttemptAt = now + RetrySchedule[nextAttempt];
            _logger.LogWarning(
                "Webhook delivery {DeliveryId} failed (attempt {AttemptCount}/{MaxAttempts}). Next attempt at {NextAttemptAt} (code={Code} error={Error})",
                deliveryId, delivery.AttemptCount, MaxAttempts,
                delivery.NextAttemptAt, responseCode, delivery.LastError);
        }

        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Operator-callable: nudge a DeadLetter row back into the active
    /// retry pool with one more attempt's worth of patience. Not
    /// invoked by any code path today; reserved for the eventual
    /// operator UI / Greenfield admin endpoint.
    /// </summary>
    public async Task ResetDeliveryForRetry(string deliveryId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var delivery = await ctx.GrinWebhookDeliveries.FindAsync(deliveryId);
        if (delivery == null) return;
        delivery.Status = GrinWebhookDeliveryStatus.Failed;
        delivery.NextAttemptAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
