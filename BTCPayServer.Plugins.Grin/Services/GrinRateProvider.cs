using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Raw HTTP fetcher for the Grin/USDT spot rate from Gate.io. Has no
/// caching of its own — wrapped at the DI layer in
/// <see cref="BackgroundFetcherRateProvider"/> for refresh + validity +
/// stale-while-revalidate, then again in <see cref="GrinRateHealth"/>
/// for operator-facing failure tracking + startup warmup. Callers
/// inject <c>IRateProvider</c> (which resolves to GrinRateHealth) — do
/// NOT inject this class directly, or you bypass the cache and pay a
/// fresh HTTP round-trip on every invoice creation.
/// </summary>
public class GrinRateProvider : IRateProvider
{
    public RateSourceInfo RateSourceInfo =>
        new("gringateio", "Grin via Gate.io", "https://www.gate.com/trade/GRIN_USDT");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrinRateProvider> _logger;

    public GrinRateProvider(IHttpClientFactory httpClientFactory, ILogger<GrinRateProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        // Hard timeout — Gate.io's spot API normally responds in <500ms.
        // If it's taking longer than 10s we'd rather fail fast and let
        // the cache fallback (or operator alert) kick in than block an
        // invoice creation for a minute.
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        const string url = "https://api.gateio.ws/api/v4/spot/tickers?currency_pair=GRIN_USDT";
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            // Network-level failure (DNS / TLS / timeout) — log the
            // class so the health summary's LastError shows something
            // actionable rather than just "request failed".
            _logger.LogWarning(
                "Grin rate fetch: HTTP request to {Url} failed at transport layer: {ExType} {Error}",
                url, ex.GetType().Name, ex.Message);
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            // Surface the response body in logs so we know whether
            // we're being rate-limited, returning HTML, etc.
            string body = "";
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (body.Length > 500) body = body.Substring(0, 500) + "…";
            }
            catch { /* ignore — body extraction is best-effort */ }
            var msg = $"Gate.io {(int)response.StatusCode} {response.StatusCode}: {body}";
            _logger.LogWarning("Grin rate fetch: {Message}", msg);
            throw new HttpRequestException(msg);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        JArray arr;
        try
        {
            arr = JArray.Parse(json);
        }
        catch (Exception ex)
        {
            var preview = json.Length > 200 ? json.Substring(0, 200) + "…" : json;
            _logger.LogWarning(
                "Grin rate fetch: response was not valid JSON: {Error}. Body preview: {Preview}",
                ex.Message, preview);
            throw;
        }

        var list = new List<PairRate>();
        foreach (var item in arr)
        {
            var bid = item["highest_bid"]?.Value<decimal>() ?? 0m;
            var ask = item["lowest_ask"]?.Value<decimal>() ?? 0m;
            BidAsk bidAsk;
            if (bid > 0 && ask > 0)
                bidAsk = new BidAsk(bid, ask);
            else
            {
                var last = item["last"]?.Value<decimal>() ?? 0m;
                if (last <= 0) continue;
                bidAsk = new BidAsk(last);
            }

            // Provide both USDT and USD pairs (USDT ≈ USD)
            list.Add(new PairRate(new CurrencyPair("GRIN", "USDT"), bidAsk));
            list.Add(new PairRate(new CurrencyPair("GRIN", "USD"), bidAsk));
        }

        if (list.Count == 0)
        {
            // Gate.io returned 200 but no usable rows — log so the
            // operator can tell "we got a response, the response was
            // empty" vs "the request itself failed."
            _logger.LogWarning(
                "Grin rate fetch: Gate.io returned 200 but no usable bid/ask/last. Possibly delisted or temporarily withdrawn from orderbook. Body length: {Length}",
                json.Length);
        }

        return list.ToArray();
    }
}
