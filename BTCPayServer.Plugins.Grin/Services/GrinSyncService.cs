using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Periodically checks sync status via:
/// 1. grin-wallet node_height (always, for wallet connectivity)
/// 2. grin node get_status (when NodeApiUrl configured, for sync % and detailed status)
/// </summary>
public class GrinSyncService : IHostedService, IDisposable
{
    private readonly GrinService _grinService;
    private readonly GrinRPCProvider _rpcProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GrinSyncService> _logger;
    private Timer _timer;
    private GrinSyncStatus _cachedStatus;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    public GrinSyncService(GrinService grinService, GrinRPCProvider rpcProvider,
        IHttpClientFactory httpClientFactory, ILogger<GrinSyncService> logger)
    {
        _grinService = grinService;
        _rpcProvider = rpcProvider;
        _httpClientFactory = httpClientFactory;
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
        var stores = await _grinService.GetAllEnabledStores();
        if (stores == null || stores.Count == 0)
        {
            _cachedStatus = null;
            return;
        }

        var settings = stores[0];

        // Try to get detailed node status if NodeApiUrl is configured
        GrinSyncStatus nodeStatus = null;
        if (!string.IsNullOrEmpty(settings.NodeApiUrl))
        {
            nodeStatus = await QueryNodeStatus(settings.NodeApiUrl);
        }

        // Always check wallet connectivity via RPC
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

            long walletHeight = 0;
            if (ok.TryGetProperty("height", out var h))
            {
                walletHeight = h.ValueKind == JsonValueKind.String
                    ? long.Parse(h.GetString()!)
                    : h.GetInt64();
            }

            if (nodeStatus != null)
            {
                // Use node's detailed status, but prefer wallet's height if available
                if (walletHeight > 0)
                    nodeStatus.NodeHeight = walletHeight;
                nodeStatus.Available = walletHeight > 0 && nodeStatus.SyncState == "synced";
                _cachedStatus = nodeStatus;
            }
            else
            {
                // Fallback: wallet-only status (no sync %)
                _cachedStatus = new GrinSyncStatus
                {
                    Available = walletHeight > 0,
                    NodeHeight = walletHeight,
                    SyncPercent = walletHeight > 0 ? 100 : 0,
                    SyncState = walletHeight > 0 ? "synced" : "syncing"
                };
            }
        }
        catch (Exception ex)
        {
            if (nodeStatus != null)
            {
                nodeStatus.Error = $"Wallet unreachable: {ex.Message}";
                nodeStatus.Available = false;
                _cachedStatus = nodeStatus;
            }
            else
            {
                _cachedStatus = new GrinSyncStatus
                {
                    Available = false,
                    SyncState = "unreachable",
                    Error = ex.Message
                };
            }
        }
    }

    private async Task<GrinSyncStatus> QueryNodeStatus(string nodeApiUrl)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var request = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "get_status",
                @params = new { }
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var url = nodeApiUrl.TrimEnd('/') + "/v2/owner";

            var response = await httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseJson = JsonSerializer.Deserialize<JsonElement>(responseBody);

            if (!responseJson.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("Ok", out var ok))
            {
                return null;
            }

            var syncStatusStr = ok.TryGetProperty("sync_status", out var ss) ? ss.GetString() : "unknown";
            long tipHeight = 0;
            if (ok.TryGetProperty("tip", out var tip) && tip.TryGetProperty("height", out var th))
            {
                tipHeight = th.ValueKind == JsonValueKind.String
                    ? long.Parse(th.GetString()!)
                    : th.GetInt64();
            }

            // Extract highest_height from sync_info if available
            long highestHeight = 0;
            if (ok.TryGetProperty("sync_info", out var syncInfo) &&
                syncInfo.TryGetProperty("highest_height", out var hh))
            {
                highestHeight = hh.ValueKind == JsonValueKind.String
                    ? long.Parse(hh.GetString()!)
                    : hh.GetInt64();
            }

            // Calculate sync percent
            int syncPercent = 0;
            if (syncStatusStr == "no_sync")
            {
                syncPercent = 100;
            }
            else if (highestHeight > 0)
            {
                syncPercent = (int)(tipHeight * 100 / highestHeight);
                syncPercent = Math.Clamp(syncPercent, 0, 99);
            }

            string syncState;
            if (syncStatusStr == "no_sync" && tipHeight > 0)
                syncState = "synced";
            else if (tipHeight > 0 || syncStatusStr != "no_sync")
                syncState = "syncing";
            else
                syncState = "unreachable";

            return new GrinSyncStatus
            {
                Available = syncState == "synced",
                NodeHeight = tipHeight,
                NetworkHeight = highestHeight > 0 ? highestHeight : tipHeight,
                SyncPercent = syncPercent,
                NodeSyncStatus = syncStatusStr,
                SyncState = syncState
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query grin node at {NodeApiUrl}", nodeApiUrl);
            return null;
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
