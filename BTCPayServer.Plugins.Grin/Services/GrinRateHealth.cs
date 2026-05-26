using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Operational health wrapper for the Grin rate feed (Gate.io GRIN/USDT).
///
/// The underlying <see cref="BackgroundFetcherRateProvider"/> already
/// handles caching + stale-while-revalidate (60s refresh, 10min
/// validity), so most transient Gate.io hiccups are absorbed
/// invisibly. What it does NOT do: surface degraded health to the
/// operator. If Gate.io is down for 15 minutes the cache expires,
/// invoice creation starts failing, and there's no signal to the
/// admin beyond log lines that nobody reads.
///
/// This service layers that signal on top:
///
///   - Counts consecutive failures
///   - Tracks the last-success timestamp
///   - Records the last error message (HTTP status + body if any)
///   - Computes a Status enum (Fresh / Stale / Failing / NeverFetched)
///     that the settings page + footer panel render as a coloured dot
///
/// It also acts as an <see cref="IHostedService"/> so the rate cache
/// gets warmed at plugin startup — the original code's first invoice
/// after a container restart had to wait for a synchronous Gate.io
/// fetch, which has bitten us in production at least once.
/// </summary>
public class GrinRateHealth : IRateProvider, IHostedService
{
    private readonly IRateProvider _inner; // BackgroundFetcherRateProvider wrapping GrinRateProvider
    private readonly ILogger<GrinRateHealth> _logger;

    private DateTimeOffset? _lastSuccess;
    private int _consecutiveFailures;
    private string _lastError;
    private PairRate[] _lastRates = System.Array.Empty<PairRate>();
    private readonly object _lock = new();

    /// <summary>Status thresholds for the operator-facing health indicator.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailingAfter = TimeSpan.FromMinutes(30);
    /// <summary>Consecutive failure count at which we flip to "failing" regardless of last-success time.</summary>
    public const int FailingFailureCount = 4;

    public GrinRateHealth(IRateProvider inner, ILogger<GrinRateHealth> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public RateSourceInfo RateSourceInfo => _inner.RateSourceInfo;

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rates = await _inner.GetRatesAsync(cancellationToken);
            lock (_lock)
            {
                _lastSuccess = DateTimeOffset.UtcNow;
                _consecutiveFailures = 0;
                _lastError = null;
                _lastRates = rates ?? System.Array.Empty<PairRate>();
            }
            return rates;
        }
        catch (Exception ex)
        {
            int count;
            lock (_lock)
            {
                _consecutiveFailures++;
                count = _consecutiveFailures;
                _lastError = ex.Message;
            }
            // Warn at increasing severity. Single failures happen — Gate.io
            // hiccups, brief network flaps — and don't deserve to scream.
            // 4+ in a row is the threshold for "operator needs to look",
            // matching FailingFailureCount.
            if (count >= FailingFailureCount)
            {
                _logger.LogError(ex,
                    "Grin rate fetch failed {Count} times in a row — RATE FEED IS DOWN. Invoice creation will start failing once cache expires (10min). Last error: {Error}",
                    count, ex.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Grin rate fetch failed (consecutive {Count}): {Error}",
                    count, ex.Message);
            }
            throw;
        }
    }

    /// <summary>Snapshot of current health, used by the settings page + footer panel.</summary>
    public GrinRateHealthStatus GetStatus()
    {
        lock (_lock)
        {
            var status = new GrinRateHealthStatus
            {
                LastSuccess = _lastSuccess,
                ConsecutiveFailures = _consecutiveFailures,
                LastError = _lastError,
                LastRates = _lastRates,
            };
            if (_lastSuccess is null)
            {
                status.State = "never_fetched";
                return status;
            }
            var age = DateTimeOffset.UtcNow - _lastSuccess.Value;
            status.Staleness = age;
            if (_consecutiveFailures >= FailingFailureCount || age > FailingAfter)
                status.State = "failing";
            else if (_consecutiveFailures >= 1 || age > StaleAfter)
                status.State = "stale";
            else
                status.State = "fresh";
            return status;
        }
    }

    // ------------------------------------------------------------------
    // IHostedService — warm the cache at plugin startup so the first
    // customer who reaches checkout doesn't pay the cold-fetch cost.
    // Fire-and-forget; a startup failure is logged but doesn't take down
    // the plugin (the BackgroundFetcherRateProvider will retry on demand).
    // ------------------------------------------------------------------
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await GetRatesAsync(cancellationToken);
                _logger.LogInformation("Grin rate cache warmed at startup");
            }
            catch (Exception ex)
            {
                // Already logged inside GetRatesAsync; this catch just
                // keeps the fire-and-forget task from unhandled-exception
                // crashing.
                _logger.LogWarning(
                    "Grin rate cache startup warmup failed (will retry on demand): {Error}",
                    ex.Message);
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public class GrinRateHealthStatus
{
    /// <summary>"fresh" | "stale" | "failing" | "never_fetched"</summary>
    public string State { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public TimeSpan? Staleness { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LastError { get; set; }
    public PairRate[] LastRates { get; set; }
}
