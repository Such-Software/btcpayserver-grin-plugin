using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Grin;

[Route("~/plugins/grin")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewProfile)]
public class UIGrinController : Controller
{
    private readonly GrinService _grinService;
    private readonly ISettingsRepository _settingsRepository;

    public UIGrinController(GrinService grinService, ISettingsRepository settingsRepository)
    {
        _grinService = grinService;
        _settingsRepository = settingsRepository;
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings()
    {
        var settings = await _settingsRepository.GetSettingAsync<GrinSettings>() ?? new GrinSettings();
        return View(settings);
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(GrinSettings settings)
    {
        await _settingsRepository.UpdateSetting(settings);
        TempData[WellKnownTempData.SuccessMessage] = "Grin settings updated.";
        return RedirectToAction(nameof(Settings));
    }
}
