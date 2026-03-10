# BTCPay Server Grin Plugin

Accept [Grin](https://grin.mw) payments in your BTCPay Server store.

Grin is a privacy-preserving cryptocurrency using MimbleWimble. Unlike Bitcoin, Grin transactions are interactive — both sender and receiver must exchange data (slatepacks) to build a transaction. This plugin manages that exchange through a checkout UI.

## How It Works

1. Merchant configures their `grin-wallet` connection in BTCPay store settings
2. Customer creates an invoice (via API or storefront integration)
3. Checkout page shows a slatepack message (with QR code) for the customer to process in their wallet
4. Customer pastes their wallet's response slatepack back into the checkout page
5. Plugin finalizes and broadcasts the transaction
6. Background service monitors confirmations and marks the invoice complete

```
Customer                    BTCPay (this plugin)              grin-wallet
   |                              |                               |
   |  --- create invoice -------> |  --- issue_invoice_tx ------> |
   |  <-- slatepack S1 ---------  |  <-- slatepack S1 ----------- |
   |                              |                               |
   |  (process S1 in wallet)      |                               |
   |                              |                               |
   |  --- paste response S2 ----> |  --- finalize_tx (S2) ------> |
   |                              |  --- post_tx ---------------> |
   |  <-- "payment broadcast" --- |                               |
   |                              |                               |
   |                            [monitor service polls every 30s] |
   |                              |  --- retrieve_txs ----------> |
   |                              |  --- node_height -----------> |
   |  <-- "confirmed" ----------- |  (confirmations >= threshold) |
```

## Requirements

- BTCPay Server v1.12+ (.NET 8)
- PostgreSQL (used by BTCPay)
- A running `grin-wallet` instance with the Owner API enabled
- A synced Grin node (the wallet connects to it)

### grin-wallet Setup

The plugin communicates with grin-wallet's Owner API using v3 encrypted JSON-RPC (ECDH key exchange + AES-256-GCM).

```bash
# Start the owner API (default port 3420)
grin-wallet owner_api
```

You'll need three things from your wallet for plugin configuration:
- **Owner API URL**: default `http://127.0.0.1:3420`
- **Wallet password**: the password used to open/unlock the wallet
- **API secret**: contents of `.owner_api_secret` in your grin-wallet data directory

## Installation

### From BTCPay Plugin Builder (recommended)

*Coming soon* — the plugin will be published to the BTCPay plugin directory.

### Manual / Development

```bash
git clone https://github.com/Such-Software/btcpayserver-grin-plugin.git
cd btcpayserver-grin-plugin
git submodule update --init --recursive

# Build the plugin
dotnet build BTCPayServer.Plugins.Grin/BTCPayServer.Plugins.Grin.csproj

# Run BTCPay with the plugin loaded
export BTCPAY_DEBUG_PLUGINS="$(pwd)/BTCPayServer.Plugins.Grin/bin/Debug/net8.0/BTCPayServer.Plugins.Grin.dll"
dotnet run --project btcpayserver/BTCPayServer --no-launch-profile
```

Set your standard BTCPay environment variables (`BTCPAY_NETWORK`, `BTCPAY_POSTGRES`, `BTCPAY_BTCEXPLORERURL`, etc.) as needed.

## Configuration

1. In BTCPay, go to your store's settings
2. Click **Grin** in the left sidebar
3. Enter your wallet's Owner API URL, password, and API secret
4. Optionally enter your Grin node's API URL for detailed sync status monitoring (shows sync percentage and phase in the BTCPay footer panel)
5. Set minimum confirmations (default: 10, roughly 10 minutes)
6. Check **Enable Grin Payments** and save
7. Click **Test Connection** to verify

See [SETUP.md](SETUP.md) for detailed deployment instructions including Docker networking with socat proxies.

## Creating Invoices

Invoices are created via the plugin's REST API:

```bash
curl -X POST "http://localhost:23000/stores/{storeId}/plugins/grin/invoices" \
  -H "Content-Type: application/json" \
  -d '{"amount": 1.5, "orderId": "order-123"}'
```

Response:
```json
{
  "invoiceId": "a1b2c3d4e5f6",
  "checkoutUrl": "http://localhost:23000/stores/{storeId}/plugins/grin/checkout/a1b2c3d4e5f6",
  "amount": 1.5,
  "amountNanogrin": 1500000000,
  "txSlateId": "...",
  "slatepackAddress": "grin1...",
  "status": "Pending"
}
```

Direct the customer to the `checkoutUrl` to complete payment.

## Invoice Lifecycle

| Status | Description |
|---|---|
| **Pending** | Invoice created, slatepack issued, awaiting customer response |
| **AwaitingResponse** | Reserved for future use |
| **Broadcast** | Transaction finalized and broadcast to the network |
| **Confirmed** | Transaction has enough confirmations (per store setting) |
| **Expired** | No response within 24 hours; wallet transaction cancelled automatically |

The background monitor service polls every 30 seconds to update confirmation counts and expire stale invoices.

## USD Pricing

The checkout page shows an approximate USD value alongside the Grin amount. The price is fetched from Gate.io's spot API and cached for 2 minutes. Sub-cent amounts display as "< $0.01".

A `GrinRateProvider` is also registered with BTCPay's rate engine, making GRIN pairs available for rate rules and scripting.

## Architecture

```
BTCPayServer.Plugins.Grin/
├── Plugin.cs                          # Service registration, nav extension
├── PluginMigrationRunner.cs           # EF Core migrations on startup
├── GrinRPCClient.cs                   # v3 encrypted Owner API client
├── Controllers/
│   ├── UIGrinController.cs            # Settings UI (store admin)
│   └── GrinCheckoutController.cs      # Checkout flow + REST API
├── Data/
│   ├── GrinDbContext.cs               # Plugin's own DB context
│   ├── GrinInvoice.cs                 # Invoice model
│   └── GrinStoreSettings.cs           # Per-store wallet config
├── Services/
│   ├── GrinService.cs                 # Business logic, price fetching
│   ├── GrinRPCProvider.cs             # Per-store RPC client cache
│   ├── GrinRateProvider.cs            # BTCPay rate engine integration
│   ├── GrinPaymentMonitorService.cs   # Background confirmation tracker
│   ├── GrinSyncService.cs            # Node sync status polling (30s)
│   ├── GrinSyncSummaryProvider.cs    # BTCPay footer panel integration
│   └── GrinDbContextFactory.cs        # DB context factory
├── Views/
│   ├── UIGrin/Settings.cshtml         # Store settings + invoice list
│   ├── GrinCheckout/Checkout.cshtml   # Payment page
│   ├── GrinCheckout/CheckoutComplete.cshtml
│   ├── GrinCheckout/CheckoutExpired.cshtml
│   ├── Shared/GrinNav.cshtml          # Sidebar nav item
│   └── Shared/Grin/GrinSyncSummary.cshtml  # Footer sync panel
├── Migrations/                        # EF Core migrations
└── Resources/img/                     # Grin logo
```

Each BTCPay store connects to its own `grin-wallet` instance, similar to how Lightning works — the plugin doesn't hold keys, the merchant runs their own wallet.

## Trust Model

Like Lightning in BTCPay, this plugin requires the merchant to run their own `grin-wallet`. The wallet password and API secret are stored in the BTCPay database. This means:

- **Self-hosted BTCPay**: Fully self-custodial. You control both the server and the wallet.
- **Third-party BTCPay**: The server operator has access to your wallet credentials. This is the same trust model as Lightning — if you don't run the server, you're trusting the operator.

Grin's interactive transaction model requires private keys for receiving, so there's no equivalent to Bitcoin's xpub-based watch-only wallets. True non-custodial operation requires self-hosting.

## Security Notes

- Wallet credentials (password, API secret) are stored in the plugin's PostgreSQL tables, not in files
- The Owner API connection uses v3 encrypted JSON-RPC (ECDH + AES-256-GCM) — even over plaintext HTTP, the RPC payload is encrypted
- The plugin never holds private keys; all signing happens in `grin-wallet`
- Invoice slatepack exchange happens over HTTPS (or whatever your BTCPay instance uses)
- Error messages shown to customers are generic — internal RPC errors are logged server-side only

## Running grin-wallet as a Service

See [contrib/systemd/](contrib/systemd/) for example systemd unit files for running the Grin node and wallet as background services.

## License

MIT
