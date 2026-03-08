using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinRateProvider : IRateProvider
{
    public RateSourceInfo RateSourceInfo =>
        new("gringateio", "Grin via Gate.io", "https://www.gate.com/trade/GRIN_USDT");

    private readonly IHttpClientFactory _httpClientFactory;

    public GrinRateProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.GetAsync(
            "https://api.gateio.ws/api/v4/spot/tickers?currency_pair=GRIN_USDT",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var arr = JArray.Parse(json);

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

        return list.ToArray();
    }
}
