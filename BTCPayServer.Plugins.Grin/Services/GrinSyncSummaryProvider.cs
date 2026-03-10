using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinSyncSummaryProvider : ISyncSummaryProvider
{
    private readonly GrinSyncService _syncService;

    public GrinSyncSummaryProvider(GrinSyncService syncService)
    {
        _syncService = syncService;
    }

    public string Partial => "Grin/GrinSyncSummary";

    public bool AllAvailable()
    {
        var status = _syncService.GetCachedStatus();
        // If no stores have Grin enabled, don't block the sync modal
        if (status == null)
            return true;
        return status.Available;
    }

    public IEnumerable<ISyncStatus> GetStatuses()
    {
        var status = _syncService.GetCachedStatus();
        if (status != null)
            yield return status;
    }
}

public class GrinSyncStatus : ISyncStatus
{
    public string PaymentMethodId { get; set; } = "GRIN";
    public bool Available { get; set; }
    public long NodeHeight { get; set; }
    public string SyncState { get; set; } // "synced", "syncing", "unreachable", "not_configured"
    public string Error { get; set; }
}
