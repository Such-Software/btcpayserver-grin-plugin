using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Grin;

[Route("stores/{storeId}/plugins/grin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIGrinController : Controller
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;

    public UIGrinController(GrinService grinService, GrinRPCProvider rpcProvider)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
    }

    [HttpGet]
    public async Task<IActionResult> Settings(string storeId)
    {
        var settings = await _grinService.GetStoreSettings(storeId) ?? new GrinStoreSettings
        {
            StoreId = storeId
        };
        return View(settings);
    }

    [HttpPost]
    public async Task<IActionResult> Settings(string storeId, GrinStoreSettings settings)
    {
        settings.StoreId = storeId;
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
