using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Grin.Payments;
using BTCPayServer.Plugins.Grin.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin;

public class PluginMigrationRunner : IHostedService
{
    private readonly GrinDbContextFactory _dbContextFactory;
    private readonly GrinService _grinService;
    private readonly StoreRepository _storeRepo;
    private readonly PaymentMethodHandlerDictionary _handlers;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(
        GrinDbContextFactory dbContextFactory,
        GrinService grinService,
        StoreRepository storeRepo,
        PaymentMethodHandlerDictionary handlers,
        ILogger<PluginMigrationRunner> logger)
    {
        _dbContextFactory = dbContextFactory;
        _grinService = grinService;
        _storeRepo = storeRepo;
        _handlers = handlers;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Grin plugin: running database migrations");
        await using var ctx = _dbContextFactory.CreateContext();
        try
        {
            await ctx.Database.MigrateAsync(cancellationToken);
            _logger.LogInformation("Grin plugin: database migrations complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grin plugin: failed to run database migrations");
            return;
        }

        // One-shot bootstrap: for every store that has the Grin
        // plugin configured + enabled but doesn't yet have GRIN-CHAIN
        // listed in BTCPay's payment-method configs, write the
        // payment-method config. Idempotent — re-running is harmless,
        // it just sets the same value.
        //
        // This catches the "operator configured Grin under an older
        // plugin version that didn't auto-write the payment-method
        // config, then upgraded" case so they don't have to re-save
        // settings to see GRIN-CHAIN appear in the store's payment
        // methods page.
        try
        {
            await BootstrapPaymentMethodConfigs(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Grin plugin: payment-method config bootstrap failed (non-fatal)");
        }
    }

    private async Task BootstrapPaymentMethodConfigs(CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(GrinPaymentMethodConstants.PaymentMethodId, out var handler))
        {
            // Handler isn't registered (shouldn't happen — Plugin.cs
            // wires it). Bail.
            return;
        }

        var enabledStores = await _grinService.GetAllEnabledStores();
        foreach (var settings in enabledStores)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var store = await _storeRepo.FindStore(settings.StoreId);
            if (store == null) continue;

            // Skip if the store already has GRIN-CHAIN configured
            // (operator already saved settings under the new flow).
            var existing = store.GetPaymentMethodConfigs();
            if (existing.ContainsKey(handler.PaymentMethodId)) continue;

            store.SetPaymentMethodConfig(handler, new GrinPaymentMethodConfig { Enabled = true });
            await _storeRepo.UpdateStore(store);
            _logger.LogInformation(
                "Bootstrapped GRIN-CHAIN payment-method config for store {StoreId}",
                settings.StoreId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
