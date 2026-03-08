using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Grin.Services;
using System.Net.Http;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Grin;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<IUIExtension>(new UIExtension("GrinNav", "store-wallets-nav"));
        services.AddHostedService<PluginMigrationRunner>();
        services.AddHostedService<GrinPaymentMonitorService>();
        services.AddSingleton<GrinRPCProvider>();
        services.AddSingleton<GrinService>();
        services.AddSingleton<GrinDbContextFactory>();
        services.AddSingleton<IRateProvider>(provider =>
            new GrinRateProvider(provider.GetRequiredService<IHttpClientFactory>()));
        services.AddDbContext<GrinDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<GrinDbContextFactory>();
            factory.ConfigureBuilder(o);
        });
    }
}
