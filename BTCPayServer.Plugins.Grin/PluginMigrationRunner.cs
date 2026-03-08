using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin;

public class PluginMigrationRunner : IHostedService
{
    private readonly GrinDbContextFactory _dbContextFactory;
    private readonly ILogger<PluginMigrationRunner> _logger;

    public PluginMigrationRunner(GrinDbContextFactory dbContextFactory, ILogger<PluginMigrationRunner> logger)
    {
        _dbContextFactory = dbContextFactory;
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
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

