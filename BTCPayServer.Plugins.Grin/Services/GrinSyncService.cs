using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Periodically checks grin-wallet node_height to provide sync status
/// for the BTCPay footer panel and settings page.
/// </summary>
public class GrinSyncService : IHostedService, IDisposable
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly ILogger<GrinSyncService> _logger;
    private Timer _timer;
    private GrinSyncStatus _cachedStatus;
    private long _previousHeight;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public GrinSyncService(GrinService grinService, GrinRPCProvider rpcProvider,
        ILogger<GrinSyncService> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _logger = logger;
    }

    public GrinSyncStatus GetCachedStatus() => _cachedStatus;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(DoWork, null, TimeSpan.FromSeconds(5), PollInterval);
        return Task.CompletedTask;
    }

    private async void DoWork(object state)
    {
        try
        {
            await CheckSync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Grin sync check failed");
        }
    }

    private async Task CheckSync()
    {
        // Find any enabled store to use for the RPC connection
        var stores = await _grinService.GetAllEnabledStores();
        if (stores == null || stores.Count == 0)
        {
            _cachedStatus = null; // No configured stores — don't show in sync panel
            return;
        }

        var settings = stores[0];

        try
        {
            var client = await _rpcProvider.GetClient(settings);
            var heightResult = await client.NodeHeight();

            if (!heightResult.TryGetProperty("Ok", out var ok))
            {
                _cachedStatus = new GrinSyncStatus
                {
                    Available = false,
                    SyncState = "unreachable",
                    Error = "Unexpected response from wallet"
                };
                return;
            }

            long height = 0;
            if (ok.TryGetProperty("height", out var h))
            {
                height = h.ValueKind == JsonValueKind.String
                    ? long.Parse(h.GetString()!)
                    : h.GetInt64();
            }

            // Determine if synced: height > 0 and advancing (or stable at tip)
            // A height of 0 means the node hasn't synced yet
            bool isSynced = height > 0 && (_previousHeight == 0 || height >= _previousHeight);
            bool isSyncing = height == 0 || (height > 0 && height < _previousHeight);

            // If height is 0, node is definitely not synced
            string syncState;
            if (height == 0)
                syncState = "syncing";
            else if (height > 0)
                syncState = "synced";
            else
                syncState = "unreachable";

            _previousHeight = height;

            _cachedStatus = new GrinSyncStatus
            {
                Available = height > 0,
                NodeHeight = height,
                SyncState = syncState
            };
        }
        catch (Exception ex)
        {
            _cachedStatus = new GrinSyncStatus
            {
                Available = false,
                SyncState = "unreachable",
                Error = ex.Message
            };
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
