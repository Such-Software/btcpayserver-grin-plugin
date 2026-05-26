using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Grin.Tests;

/// <summary>
/// Verifies the operator-facing health-state transitions in
/// <see cref="GrinRateHealth"/>. These determine which dot the
/// settings page + BTCPay sync footer render, so getting them wrong
/// means real outages either scream too loudly (false alarm fatigue)
/// or stay silent (operator finds out from a customer ticket).
/// </summary>
public class GrinRateHealthTests
{
    private static GrinRateHealth Build(IRateProvider inner) =>
        new(inner, NullLogger<GrinRateHealth>.Instance);

    [Fact]
    public async Task NeverFetched_StateIsNeverFetched()
    {
        var health = Build(new FakeProvider());
        var status = health.GetStatus();
        Assert.Equal("never_fetched", status.State);
        Assert.Null(status.LastSuccess);
        Assert.Equal(0, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task FirstSuccess_StateIsFresh()
    {
        var fake = new FakeProvider();
        var health = Build(fake);
        await health.GetRatesAsync(CancellationToken.None);
        var status = health.GetStatus();
        Assert.Equal("fresh", status.State);
        Assert.NotNull(status.LastSuccess);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task SingleFailureAfterSuccess_StateIsStale()
    {
        // After 1 success then 1 failure, the cache is still considered
        // recoverable — operator should see "stale" not "failing".
        var fake = new FakeProvider();
        var health = Build(fake);
        await health.GetRatesAsync(CancellationToken.None);
        fake.ThrowOnNext("connection refused");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => health.GetRatesAsync(CancellationToken.None));
        var status = health.GetStatus();
        Assert.Equal("stale", status.State);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Equal("connection refused", status.LastError);
    }

    [Fact]
    public async Task FourConsecutiveFailures_StateIsFailing()
    {
        // Hits the FailingFailureCount threshold (4) — operator must
        // see red even if last success was very recent.
        var fake = new FakeProvider();
        var health = Build(fake);
        await health.GetRatesAsync(CancellationToken.None);
        for (int i = 0; i < 4; i++)
        {
            fake.ThrowOnNext($"failure {i + 1}");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => health.GetRatesAsync(CancellationToken.None));
        }
        var status = health.GetStatus();
        Assert.Equal("failing", status.State);
        Assert.Equal(4, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task SuccessAfterFailures_ResetsCounter()
    {
        // Recovery scenario: 3 failures, then a successful refresh.
        // Counter must clear; state must return to fresh. Otherwise a
        // transient blip would stick the operator with a permanent red
        // dot until they restarted BTCPay.
        var fake = new FakeProvider();
        var health = Build(fake);
        await health.GetRatesAsync(CancellationToken.None);
        for (int i = 0; i < 3; i++)
        {
            fake.ThrowOnNext("transient");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => health.GetRatesAsync(CancellationToken.None));
        }
        await health.GetRatesAsync(CancellationToken.None);
        var status = health.GetStatus();
        Assert.Equal("fresh", status.State);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task LastRates_PreservedAcrossFailures()
    {
        // While the rate is degraded, callers should still be able to
        // ask for the LAST KNOWN GOOD rates via GetStatus().LastRates.
        // BTCPay's BackgroundFetcherRateProvider already does
        // stale-while-revalidate on the GetRatesAsync path; this test
        // is just locking in that GrinRateHealth.GetStatus surfaces
        // the same information for the UI.
        var fake = new FakeProvider();
        fake.NextRates = new[]
        {
            new PairRate(new CurrencyPair("GRIN", "USDT"), new BidAsk(0.50m)),
        };
        var health = Build(fake);
        await health.GetRatesAsync(CancellationToken.None);

        fake.ThrowOnNext("gate.io down");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => health.GetRatesAsync(CancellationToken.None));

        var status = health.GetStatus();
        Assert.Single(status.LastRates);
        Assert.Equal("GRIN", status.LastRates[0].CurrencyPair.Left);
        Assert.Equal("USDT", status.LastRates[0].CurrencyPair.Right);;
    }

    private class FakeProvider : IRateProvider
    {
        public PairRate[] NextRates { get; set; } = System.Array.Empty<PairRate>();
        private string _throwOnNext;

        public RateSourceInfo RateSourceInfo => new("test", "Test Provider", "https://example.com");

        public void ThrowOnNext(string message) => _throwOnNext = message;

        public Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
        {
            if (_throwOnNext != null)
            {
                var msg = _throwOnNext;
                _throwOnNext = null;
                throw new InvalidOperationException(msg);
            }
            return Task.FromResult(NextRates);
        }
    }
}
