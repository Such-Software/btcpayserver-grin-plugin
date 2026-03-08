using System.Collections.Concurrent;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinRPCProvider
{
    private readonly ConcurrentDictionary<string, GrinRPCClient> _clients = new();
    private readonly ILoggerFactory _loggerFactory;

    public GrinRPCProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public async Task<GrinRPCClient> GetClient(GrinStoreSettings settings)
    {
        if (_clients.TryGetValue(settings.StoreId, out var existing))
            return existing;

        var client = new GrinRPCClient(_loggerFactory.CreateLogger<GrinRPCClient>());
        client.Configure(settings.OwnerApiUrl, settings.WalletPassword, settings.ApiSecret);
        await client.InitSession();

        _clients[settings.StoreId] = client;
        return client;
    }

    public void InvalidateClient(string storeId)
    {
        _clients.TryRemove(storeId, out _);
    }

    public bool HasClient(string storeId) => _clients.ContainsKey(storeId);
}
