using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin;

[Route("stores/{storeId}/plugins/grin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIGrinController : Controller
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly GrinRateHealth _rateHealth;
    private readonly ILogger<UIGrinController> _logger;

    public UIGrinController(
        GrinService grinService,
        GrinRPCProvider rpcProvider,
        GrinRateHealth rateHealth,
        ILogger<UIGrinController> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _rateHealth = rateHealth;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Settings(string storeId)
    {
        var settings = await _grinService.GetStoreSettings(storeId) ?? new GrinStoreSettings
        {
            StoreId = storeId
        };
        ViewBag.Invoices = await _grinService.GetInvoicesByStore(storeId);
        ViewBag.Balance = await TryGetWalletBalance(settings);
        ViewBag.RateHealth = _rateHealth.GetStatus();
        return View(settings);
    }

    /// <summary>
    /// Best-effort wallet balance fetch for the settings page. Returns
    /// null on any failure (wallet not configured, RPC unreachable,
    /// unexpected response shape) so the settings page still renders
    /// when the wallet is down. The balance is purely informational —
    /// nothing else on the page depends on it.
    /// </summary>
    private async Task<WalletBalanceView> TryGetWalletBalance(GrinStoreSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OwnerApiUrl) ||
            string.IsNullOrWhiteSpace(settings.ApiSecret))
        {
            return null;
        }
        try
        {
            var client = await _rpcProvider.GetClient(settings);
            var summary = await client.GetSummaryInfo();
            // Grin owner_api retrieve_summary_info returns:
            //   { "Ok": [ height_int, { amount_currently_spendable: "...",
            //                           amount_awaiting_confirmation: "...",
            //                           amount_immature: "...",
            //                           amount_locked: "...",
            //                           last_confirmed_height: "...",
            //                           total: "..." } ] }
            // Values are nanogrin strings (1 GRIN = 1e9 nanogrin).
            if (!summary.TryGetProperty("Ok", out var ok)) return null;
            if (ok.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            if (ok.GetArrayLength() < 2) return null;
            var info = ok[1];
            return new WalletBalanceView
            {
                SpendableNanogrin = ParseNanogrin(info, "amount_currently_spendable"),
                AwaitingNanogrin = ParseNanogrin(info, "amount_awaiting_confirmation"),
                ImmatureNanogrin = ParseNanogrin(info, "amount_immature"),
                LockedNanogrin = ParseNanogrin(info, "amount_locked"),
                TotalNanogrin = ParseNanogrin(info, "total"),
                LastConfirmedHeight = ParseHeight(info, "last_confirmed_height"),
            };
        }
        catch (Exception ex)
        {
            return new WalletBalanceView { Error = ex.Message };
        }
    }

    private static long ParseNanogrin(System.Text.Json.JsonElement info, string key)
    {
        if (!info.TryGetProperty(key, out var el)) return 0;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String =>
                long.TryParse(el.GetString(), out var v) ? v : 0,
            System.Text.Json.JsonValueKind.Number => el.GetInt64(),
            _ => 0,
        };
    }

    private static ulong ParseHeight(System.Text.Json.JsonElement info, string key)
    {
        if (!info.TryGetProperty(key, out var el)) return 0;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String =>
                ulong.TryParse(el.GetString(), out var v) ? v : 0,
            System.Text.Json.JsonValueKind.Number => el.GetUInt64(),
            _ => 0,
        };
    }

    public class WalletBalanceView
    {
        public long SpendableNanogrin { get; set; }
        public long AwaitingNanogrin { get; set; }
        public long ImmatureNanogrin { get; set; }
        public long LockedNanogrin { get; set; }
        public long TotalNanogrin { get; set; }
        public ulong LastConfirmedHeight { get; set; }
        public string Error { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Settings(string storeId, GrinStoreSettings settings)
    {
        settings.StoreId = storeId;

        // SSRF guard on operator-editable URL fields. OwnerApiUrl +
        // NodeApiUrl legitimately point at the same docker network as
        // BTCPay (the grin-wallet RPC container), so we allow private
        // ranges with a warning rather than hard-rejecting. The strict
        // rejects are: bad scheme, embedded credentials, IMDS hosts.
        // See UrlSafetyValidator for the full ruleset.
        var ownerCheck = UrlSafetyValidator.Validate(settings.OwnerApiUrl, "Wallet owner API URL");
        var nodeCheck = UrlSafetyValidator.Validate(settings.NodeApiUrl, "Node API URL", allowEmpty: true);
        var webhookCheck = UrlSafetyValidator.Validate(settings.WebhookUrl, "Webhook URL", allowEmpty: true);
        var errors = new System.Collections.Generic.List<string>();
        errors.AddRange(ownerCheck.Errors);
        errors.AddRange(nodeCheck.Errors);
        errors.AddRange(webhookCheck.Errors);
        if (errors.Count > 0)
        {
            TempData[WellKnownTempData.ErrorMessage] = string.Join(" ", errors);
            ViewBag.Invoices = await _grinService.GetInvoicesByStore(storeId);
            ViewBag.Balance = await TryGetWalletBalance(settings);
            ViewBag.RateHealth = _rateHealth.GetStatus();
            return View(settings);
        }
        // Warnings don't block save but get logged for operator visibility.
        foreach (var w in ownerCheck.Warnings) _logger.LogWarning("Grin store {StoreId} settings warning: {Warning}", storeId, w);
        foreach (var w in nodeCheck.Warnings) _logger.LogWarning("Grin store {StoreId} settings warning: {Warning}", storeId, w);
        foreach (var w in webhookCheck.Warnings) _logger.LogWarning("Grin store {StoreId} settings warning: {Warning}", storeId, w);

        _rpcProvider.InvalidateClient(storeId);
        await _grinService.SaveStoreSettings(settings);
        TempData[WellKnownTempData.SuccessMessage] = "Grin settings updated.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnection(string storeId)
    {
        var settings = await _grinService.GetStoreSettings(storeId);
        if (settings == null)
        {
            TempData[WellKnownTempData.ErrorMessage] = "Please save settings first.";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        try
        {
            var client = await _rpcProvider.GetClient(settings);
            var height = await client.NodeHeight();
            // Response is {"Ok": {"header_hash": "...", "height": 123, ...}}
            if (height.TryGetProperty("Ok", out var ok))
            {
                var heightEl = ok.GetProperty("height");
                var heightValue = heightEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? ulong.Parse(heightEl.GetString())
                    : heightEl.GetUInt64();
                TempData[WellKnownTempData.SuccessMessage] = $"Connected to grin-wallet. Node height: {heightValue}";
            }
            else
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Unexpected response: {height}";
            }
        }
        catch (Exception ex)
        {
            TempData[WellKnownTempData.ErrorMessage] = $"Connection failed: {ex.Message}";
        }

        return RedirectToAction(nameof(Settings), new { storeId });
    }
}
