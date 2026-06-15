using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Background worker that drains <see cref="GrinWebhookDeliveryService"/>'s
/// queue. Ticks every 5s, claims up to <c>BatchSize</c> ready rows,
/// attempts each one, and feeds results back through the service's
/// <c>MarkDelivered</c> / <c>MarkFailed</c> methods.
///
/// Ticks are sequential within a worker — multi-pm2-worker plugin
/// support would require a row-level lock (FOR UPDATE SKIP LOCKED)
/// in <c>GetReadyDeliveries</c>. Single-instance BTCPay deploys (the
/// expected topology) don't need it. If the plugin ever clusters,
/// either add the lock or partition the queue by storeId hash.
///
/// HTTP timeout is 30s. Beyond that we mark the attempt as failed
/// and let the backoff schedule retry — a webhook receiver that
/// can't respond in 30s either isn't healthy or is doing too much
/// work synchronously, both of which are worth surfacing.
/// </summary>
public class GrinWebhookDeliveryWorker : IHostedService, IDisposable
{
    private readonly GrinWebhookDeliveryService _deliveryService;
    private readonly GrinService _grinService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrinWebhookDeliveryWorker> _logger;
    private Timer _timer;
    private int _ticking;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(30);
    private const int BatchSize = 50;

    public GrinWebhookDeliveryWorker(
        GrinWebhookDeliveryService deliveryService,
        GrinService grinService,
        IHttpClientFactory httpClientFactory,
        ILogger<GrinWebhookDeliveryWorker> logger)
    {
        _deliveryService = deliveryService;
        _grinService = grinService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Grin webhook delivery worker started");
        // Initial 5s delay so the worker doesn't race the migration runner
        // on first boot. Then tick every TickInterval.
        _timer = new Timer(_ => _ = DoTick(), null, TimeSpan.FromSeconds(5), TickInterval);
        return Task.CompletedTask;
    }

    private async Task DoTick()
    {
        // Reentrancy guard. Timer callbacks fire on a thread pool
        // thread regardless of whether the prior callback finished —
        // a slow tick (Medusa is timing out, 50 retries lined up)
        // would otherwise re-enter and double-charge attempts.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            _logger.LogDebug("Grin webhook delivery tick already in flight, skipping");
            return;
        }

        try
        {
            var ready = await _deliveryService.GetReadyDeliveries(BatchSize);
            if (ready.Count == 0) return;

            foreach (var delivery in ready)
            {
                try
                {
                    var settings = await _grinService.GetStoreSettings(delivery.StoreId);
                    if (settings == null)
                    {
                        // Store was deleted between enqueue and now. No
                        // recovery possible — mark dead-letter so the
                        // row stops getting picked up.
                        await _deliveryService.MarkFailed(
                            delivery.Id, null, "Store settings not found");
                        continue;
                    }

                    var (ok, code, error) = await Send(delivery, settings.WebhookSecret);
                    if (ok)
                    {
                        // ok=true is only returned when Send saw a real HTTP
                        // response code (the 2xx branch), so code is non-null
                        // by construction.
                        await _deliveryService.MarkDelivered(delivery.Id, code ?? 0);
                    }
                    else
                    {
                        await _deliveryService.MarkFailed(delivery.Id, code, error);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Webhook delivery {DeliveryId} threw during dispatch loop", delivery.Id);
                    try
                    {
                        await _deliveryService.MarkFailed(delivery.Id, null, ex.Message);
                    }
                    catch (Exception markEx)
                    {
                        _logger.LogError(markEx,
                            "Failed to mark delivery {DeliveryId} as failed; row will be retried as Pending",
                            delivery.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grin webhook delivery worker tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Single delivery attempt. Returns (ok, http-status-code-or-null,
    /// error-message). On HTTP 2xx returns (true, code, null). On
    /// non-2xx returns (false, code, "non-2xx"). On transport
    /// failure returns (false, null, message).
    ///
    /// Signature is recomputed per attempt against the LIVE secret
    /// — a secret rotation between enqueue and dispatch causes
    /// in-flight retries to land with the new signature. The
    /// merchant either accepts the new secret (intended) or fails
    /// verification (and the worker dead-letters after the backoff
    /// schedule exhausts). Either is correct behavior.
    /// </summary>
    private async Task<(bool ok, int? code, string error)> Send(
        GrinWebhookDelivery delivery, string webhookSecret)
    {
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(delivery.Payload);

            var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
            {
                Content = new ByteArrayContent(payloadBytes),
            };
            request.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var sig = WebhookSignature.Compute(webhookSecret, payloadBytes);
            if (!string.IsNullOrEmpty(sig))
                request.Headers.Add("btcpay-sig", sig);
            request.Headers.Add("User-Agent", "btcpayserver-grin-plugin/1.0");
            request.Headers.Add("btcpay-grin-delivery-id", delivery.Id);
            request.Headers.Add("btcpay-grin-attempt", (delivery.AttemptCount + 1).ToString());

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = HttpTimeout;
            var response = await client.SendAsync(request);
            var code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return (true, code, null);
            return (false, code, $"non-2xx ({code})");
        }
        catch (TaskCanceledException)
        {
            return (false, null, $"timeout ({HttpTimeout.TotalSeconds:F0}s)");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Grin webhook delivery worker stopping");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
