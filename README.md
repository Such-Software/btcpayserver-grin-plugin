# BTCPay Server Grin Plugin

Accept [Grin](https://grin.mw) payments in your BTCPay Server store.

Grin is a privacy-preserving cryptocurrency using MimbleWimble. Unlike Bitcoin, Grin transactions are interactive — both sender and receiver must exchange data (slatepacks) to build a transaction. This plugin manages that exchange through a checkout UI.

## How It Works

Grin is a first-class payment method in BTCPay. Operators enable it on
a store the same way they enable Bitcoin or Lightning; integrators
create invoices through BTCPay's standard Greenfield API and receive
the same `InvoicePaymentSettled` webhook BTCPay delivers for every
other payment method.

1. Merchant configures their `grin-wallet` connection in BTCPay store settings
2. Integrator creates a BTCPay invoice via Greenfield with Grin as the payment method (or a customer hits the BTCPay-hosted checkout)
3. Plugin's `IPaymentMethodHandler` issues a Grin wallet tx + populates the payment prompt with the slatepack address + message
4. Customer pastes their wallet's response slatepack on the checkout page; plugin finalizes + broadcasts the transaction
5. Background service monitors confirmations and, on settlement, hands a `PaymentData` to BTCPay's `PaymentService` — same code path Bitcoin and Lightning use
6. BTCPay's event aggregator + WebhookSender fire the standard `InvoicePaymentSettled` event to every webhook URL the operator has subscribed

```
Integrator                BTCPay invoice flow            Plugin (handler)             grin-wallet
   |                              |                              |                          |
   |  POST /api/v1/.../invoices > |  --- ConfigurePrompt() ----> |  --- issue_invoice_tx -> |
   |                              |                              |  <-- slatepack S1 ------ |
   |  <-- invoice + prompt ------ |  <-- Destination + Details - |                          |
   |                              |                              |                          |
   |       (customer is sent to the BTCPay checkout)             |                          |
   |                              |                              |                          |
                                  |   customer pastes S2 ----->  |  --- finalize_tx (S2) -> |
                                  |                              |  --- post_tx ----------> |
                                  |                              |                          |
                                  |  [monitor polls every 30s, calls retrieve_txs]          |
                                  |                              |                          |
                                  |  <-- PaymentService.AddPayment(Settled) --              |
   <- InvoicePaymentSettled       |  --- event aggregator fires PaymentSettled ---          |
      webhook (BTCPay-native)     |  --- WebhookSender delivers to subscribed URLs          |
```

## Requirements

- BTCPay Server v2.3.9+ (.NET 10)
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

# Build the plugin (.NET 10 SDK required)
dotnet build BTCPayServer.Plugins.Grin/BTCPayServer.Plugins.Grin.csproj

# Run the tests
dotnet test BTCPayServer.Plugins.Grin.Tests/BTCPayServer.Plugins.Grin.Tests.csproj

# Run BTCPay with the plugin loaded
export BTCPAY_DEBUG_PLUGINS="$(pwd)/BTCPayServer.Plugins.Grin/bin/Debug/net10.0/BTCPayServer.Plugins.Grin.dll"
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

Invoices use BTCPay's standard Greenfield API. Pick `GRIN-CHAIN` in
the `paymentMethods` array (or omit `paymentMethods` to enable every
method the store has configured):

```bash
curl -X POST "https://your.btcpay/api/v1/stores/{storeId}/invoices" \
  -H "Authorization: token <btcpay-api-key>" \
  -H "Content-Type: application/json" \
  -d '{
    "amount": "1.5",
    "currency": "USD",
    "checkout": { "paymentMethods": ["GRIN-CHAIN"] }
  }'
```

The response is the standard BTCPay invoice envelope; redirect the
customer to `checkoutLink` to complete payment. The Grin payment
prompt — slatepack address + message + tx slate id — is available
under `paymentMethods[].details` (or the `/api/v1/stores/{storeId}/invoices/{id}/payment-methods`
endpoint).

## Webhooks

Subscribe a webhook in **Store Settings → Webhooks** with the
`Invoice payment settled` event selected. The plugin records each
on-chain confirmation as a standard `PaymentData` row; BTCPay's
event aggregator + built-in `WebhookSender` deliver the canonical
`InvoicePaymentSettled` event to every subscribed URL, signed with
the BTCPay-native `BTCPay-Sig: sha256=<hmac>` header. No plugin-
specific webhook configuration is required.

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
│   └── GrinCheckoutController.cs      # Customer-facing checkout flow
├── Data/
│   ├── GrinDbContext.cs               # Plugin's own DB context
│   ├── GrinInvoice.cs                 # Internal invoice model (links to BTCPay invoice)
│   ├── GrinStoreSettings.cs           # Per-store wallet config
│   └── GrinWebhookDelivery.cs         # Internal queue (legacy direct route only)
├── Payments/                          # IPaymentMethodHandler integration
│   ├── GrinPaymentMethodConstants.cs  # "GRIN-CHAIN" PaymentMethodId
│   ├── GrinPaymentMethodHandler.cs    # ConfigurePrompt / BeforeFetchingRates
│   ├── GrinPaymentLinkExtension.cs    # Slatepack payment-link surface
│   ├── GrinPaymentPromptDetails.cs    # JSON shape stored on PaymentPrompt
│   └── GrinPaymentMethodConfig.cs     # Per-store payment-method config
├── Services/
│   ├── GrinService.cs                 # Business logic, price fetching
│   ├── GrinRPCProvider.cs             # Per-store RPC client cache
│   ├── GrinRateProvider.cs            # BTCPay rate engine integration
│   ├── GrinPaymentMonitorService.cs   # Background confirmation tracker
│   ├── GrinSettlementDispatcher.cs    # Settlement → PaymentService.AddPayment
│   ├── GrinSyncService.cs             # Node sync status polling (30s)
│   ├── GrinSyncSummaryProvider.cs     # BTCPay footer panel integration
│   ├── UrlSafetyValidator.cs          # SSRF guard on settings URLs
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

- Wallet credentials (password, API secret, webhook secret) are
  stored in the plugin's PostgreSQL tables, **encrypted at rest** via
  ASP.NET Core's `IDataProtector` (v1.0.10+). Key ring lives in
  BTCPay's existing DataProtection directory — back it up.
- The Owner API connection uses v3 encrypted JSON-RPC (ECDH +
  AES-256-GCM) — even over plaintext HTTP, the RPC payload is encrypted
- The plugin never holds private keys; all signing happens in `grin-wallet`
- Invoice slatepack exchange happens over HTTPS (or whatever your BTCPay instance uses)
- Error messages shown to customers are generic — internal RPC errors are logged server-side only
- Chain-reorg detection (v1.0.10+) re-checks confirmed invoices for
  up to 2 hours after settlement and fires an `InvoiceInvalid`
  webhook if the payment is later orphaned

For the full security policy + responsible disclosure process, see
[SECURITY.md](SECURITY.md).

## Limitations & Known Issues

- **QR code on checkout is hard to scan** with mobile cameras. The
  slatepack payload is 500–1500 chars, producing a Version 30+ QR
  with ~1px modules at default zoom. Workaround: customers paste the
  slatepack manually. Fix (animated multi-frame QR via UR / BBQr) is
  on the roadmap.
- **Integration tests** run against pure helpers (reorg decision,
  HMAC signature, encrypted columns, URL safety). A real
  grin-wallet integration test isn't in CI yet — manual end-to-end
  is documented in `SETUP.md` under "Accept your first payment."

See [CHANGELOG.md](CHANGELOG.md) for the full release history and a
detailed view of what shipped when.

## Running grin-wallet as a Service

See [contrib/systemd/](contrib/systemd/) for example systemd unit files for running the Grin node and wallet as background services.

## Contributing

PRs welcome. Bugs, feature requests, and design discussion go in
GitHub issues. Security issues go via email — see
[SECURITY.md](SECURITY.md). The full contribution guide lives in
[CONTRIBUTING.md](CONTRIBUTING.md).

Quick PR checklist:

- `dotnet build` clean
- `dotnet test` green (all 23+ tests)
- `CHANGELOG.md` updated under the current version
- One logical change per PR

## License

MIT
