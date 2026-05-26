using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Grin;

/// <summary>
/// Public unauthenticated GRIN→fiat spot rate endpoint. Mirrors the
/// pattern used by xmrcheckout and wowcheckout: surface the rate the
/// invoice path already uses internally so downstream consumers (e.g.
/// a multi-tenant storefront) can display a live USD reference price
/// for GRIN-denominated products without scraping Gate.io directly.
///
/// We can't use BTCPay's built-in <c>/api/rates</c> endpoint for this —
/// plugin-registered rate providers are visible to the rate-rule
/// engine internally but aren't exposed on that public endpoint, so
/// requesting <c>currencyPairs=GRIN_USDT</c> there returns an empty
/// array. Hence this dedicated route.
///
/// No auth: this is the same data the public checkout flow consumes,
/// and the upstream Gate.io quote isn't sensitive. Caching is
/// inherited from <see cref="BackgroundFetcherRateProvider"/> which
/// wraps <see cref="GrinRateProvider"/> with a 60s refresh window.
/// </summary>
[Route("plugins/grin")]
[AllowAnonymous]
public class PublicRateController : Controller
{
    private readonly GrinRateHealth _rateHealth;

    public PublicRateController(GrinRateHealth rateHealth)
    {
        _rateHealth = rateHealth;
    }

    [HttpGet("rate")]
    public async Task<IActionResult> GetRate(CancellationToken cancellationToken)
    {
        // GrinRateHealth wraps BackgroundFetcherRateProvider — calling
        // GetRatesAsync hits the cache when warm (typical case after
        // the IHostedService startup-warmup ran), or pays the Gate.io
        // round-trip when the cache is cold (rare; first ~60s after
        // BTCPay restart only).
        var rates = await _rateHealth.GetRatesAsync(cancellationToken);
        var pair = rates?.FirstOrDefault(r =>
            r.CurrencyPair.Left == "GRIN" && r.CurrencyPair.Right == "USDT");
        if (pair is null)
        {
            return StatusCode(503, new
            {
                error = "Grin rate not yet available",
                state = _rateHealth.GetStatus().State,
            });
        }

        // BidAsk gives us bid + ask; the rate we charge customers is
        // the bid (conservative when converting OUT of crypto). Match
        // that here so the storefront's USD reference matches what
        // they'd actually be quoted at checkout.
        var rate = pair.BidAsk.Bid;
        var status = _rateHealth.GetStatus();

        return Ok(new
        {
            rate = rate.ToString("0.########"),
            currency = "USDT",  // Gate.io's GRIN pair is against USDT, which trades 1:1 with USD
            source = "gate.io",
            base_currency = "GRIN",
            quoted_at = status.LastSuccess?.UtcDateTime,
            state = status.State,            // "fresh" | "stale" | "failing" | "never_fetched"
            consecutive_failures = status.ConsecutiveFailures,
        });
    }
}
