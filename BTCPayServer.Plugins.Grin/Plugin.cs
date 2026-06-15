using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Grin.Services;
using BTCPayServer.Rating;
using System;
using System.Net.Http;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin;

public class Plugin : BaseBTCPayServerPlugin
{
    // Tracks the BTCPay Server release we actually build + test against
    // (see the git submodule in `btcpayserver/`). Was `>=1.12.0` which
    // pre-dates the .NET 10 cutover and would silently install on hosts
    // we've never validated against. README + SETUP.md both require
    // BTCPayServer 2.3.9+ since that's where .NET 10 + the antiforgery
    // filter changes that this plugin compiles against landed.
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.9" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<IUIExtension>(new UIExtension("GrinNav", "store-wallets-nav"));
        services.AddHostedService<PluginMigrationRunner>();
        services.AddHostedService<GrinPaymentMonitorService>();
        services.AddHostedService<GrinWebhookDeliveryWorker>();
        services.AddSingleton<GrinRPCProvider>();
        services.AddSingleton<GrinService>();
        services.AddSingleton<GrinWebhookDeliveryService>();
        services.AddSingleton<GrinSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<GrinSyncService>());
        services.AddSingleton<ISyncSummaryProvider, GrinSyncSummaryProvider>();
        services.AddSingleton<GrinDbContextFactory>();

        // Rate-provider chain — three layers:
        //   1. GrinRateProvider           — raw HTTP fetch from Gate.io,
        //                                   no caching, structured logging
        //   2. BackgroundFetcherRateProvider — BTCPay's built-in caching
        //                                      wrapper. 60s refresh,
        //                                      10min validity, stale-
        //                                      while-revalidate.
        //   3. GrinRateHealth             — health tracking + startup
        //                                   warmup. This is what every
        //                                   IRateProvider consumer
        //                                   actually receives via DI.
        //
        // The cardinal sin to avoid: injecting GrinRateProvider directly
        // anywhere. That bypasses every layer of caching + monitoring
        // and pays a synchronous Gate.io round-trip on every call.
        services.AddSingleton<GrinRateProvider>(provider =>
            new GrinRateProvider(
                provider.GetRequiredService<IHttpClientFactory>(),
                provider.GetRequiredService<ILogger<GrinRateProvider>>()));
        services.AddSingleton<BackgroundFetcherRateProvider>(provider =>
            new BackgroundFetcherRateProvider(provider.GetRequiredService<GrinRateProvider>())
            {
                RefreshRate = TimeSpan.FromSeconds(60),
                ValidatyTime = TimeSpan.FromMinutes(10),
            });
        services.AddSingleton<GrinRateHealth>(provider =>
            new GrinRateHealth(
                provider.GetRequiredService<BackgroundFetcherRateProvider>(),
                provider.GetRequiredService<ILogger<GrinRateHealth>>()));
        services.AddHostedService(sp => sp.GetRequiredService<GrinRateHealth>());
        services.AddSingleton<IRateProvider>(provider =>
            provider.GetRequiredService<GrinRateHealth>());

        services.AddDbContext<GrinDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<GrinDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
    }
}
