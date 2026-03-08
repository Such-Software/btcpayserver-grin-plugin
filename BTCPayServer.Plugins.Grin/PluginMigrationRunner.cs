using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

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
        await using var ctx = _dbContextFactory.CreateContext();
        try
        {
            await ctx.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception)
        {
            _logger.LogInformation("No migrations found, creating schema from model");
            await ctx.Database.EnsureCreatedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

