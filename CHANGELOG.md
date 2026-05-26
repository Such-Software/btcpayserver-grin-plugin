# Changelog

All notable changes to the BTCPay Server Grin plugin will be documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/). Patch-version-only releases
are skipped when they fix a single bug — see `git log` for the full history.

## [1.2.0] — 2026-05-26

### Added

- **Public rate endpoint** — `GET /plugins/grin/rate` returns the
  cached GRIN/USDT spot rate (sourced from Gate.io via the existing
  `BackgroundFetcherRateProvider`). No auth required.

  Use case: downstream services (e.g. an e-commerce storefront
  pricing products in GRIN) can display a live USD reference next
  to the headline GRIN amount, matching what the customer would
  actually be quoted at checkout. We can't pull this from BTCPay's
  built-in `/api/rates` endpoint — plugin-registered rate providers
  aren't exposed there — so a dedicated route was the cleanest path.

  Response shape:
  ```json
  {
    "rate": "0.50800000",
    "currency": "USDT",
    "base_currency": "GRIN",
    "source": "gate.io",
    "quoted_at": "2026-05-26T19:30:00Z",
    "state": "fresh",
    "consecutive_failures": 0
  }
  ```

## [1.1.0] — 2026-05-26

First "promoted" public release. Same code as v1.0.11 — the version
bump signals the project is past the v1.0.x soak window, has survived
a production incident + recovery, and is ready for community use.

Smoke-tested end-to-end on suchshop.lol against a live grin-wallet +
grin-node:
- Invoice creation (was the v1.0.11 P0 — confirmed fixed).
- Slatepack flow → broadcast → confirmation.
- BackgroundFetcherRateProvider serving cached rate (no live Gate.io
  call per invoice) — `GrinRateHealth` shows green "fresh" dot.
- Auto-redirect on broadcast (v1.0.9) still firing.

No code or wire-protocol changes vs v1.0.11.

## [1.0.11] — 2026-05-26

Reliability follow-up to v1.0.10. A real production incident on
suchshop.lol — first invoice after a BTCPay container restart 400'd
because `GrinRateProvider` had no caching: every invoice creation
made a fresh HTTP request to Gate.io, and the cold first call
failed (transient network, slow TLS, or empty bid/ask — exact root
cause unrecoverable from logs).

### Added

- **Cached rate provider** — `GrinRateProvider` is now wrapped at the
  DI layer in BTCPay's `BackgroundFetcherRateProvider` (60s refresh,
  10min validity, stale-while-revalidate). Invoice creation no
  longer pays a fresh Gate.io HTTP round-trip per invoice.
- **Startup warm-up** — `GrinRateHealth` is registered as
  `IHostedService` and pre-fetches the rate at plugin load, so the
  first customer who reaches checkout doesn't pay the cold-cache
  cost.
- **Rate-feed health tracking** — `GrinRateHealth` counts
  consecutive failures, records last-success timestamp, and surfaces
  a state enum (Fresh / Stale / Failing / NeverFetched) that the
  settings page + BTCPay sync footer render as a coloured dot:
  - 0-5 min since last success + 0-1 failures → green "healthy"
  - 5-30 min OR 2-3 failures → amber "degraded"
  - >30 min OR 4+ failures → red "FAILING"
- **Operator-visible warnings** — when state is failing, the
  settings page shows an explicit alert ("invoice creation will
  start failing once cache expires"), and the BTCPay-wide sync
  footer (the one that already shows Grin node sync) shows the
  same status. An operator who never opens the store settings page
  still sees the warning from anywhere in BTCPay.
- **Structured error logging** in `GrinRateProvider` — fetch
  failures now log the HTTP status, response body preview (up to
  500 chars), and transport-layer error class. Eliminates the
  "(400): " mystery from the v1.0.10 incident.
- **HTTP timeout** — `GrinRateProvider` now caps each Gate.io
  request at 10 seconds (was effectively unlimited via the default
  HttpClient timeout). Fails fast on slow/hung requests so the
  health counter increments visibly.
- 6 new tests covering rate-health state transitions (never_fetched
  → fresh → stale → failing → recovery).

### Fixed

- First invoice after a BTCPay restart no longer depends on a live
  Gate.io call (was the source of the 2026-05-26 P0 incident).
- Repeated rate-fetch failures are now visible to the operator
  before customers start seeing 400s at checkout.
- **`POST /stores/{storeId}/plugins/grin/invoices` was silently
  returning HTTP 400 with empty body** after BTCPay 2.3.9 — that
  release added a global `UIControllerAntiforgeryTokenAttribute`
  filter that rejects cookie-less POSTs before the action runs.
  Added `[IgnoreAntiforgeryToken]` on the external API action only;
  the form-based checkout submission still gets antiforgery
  protection via the global filter.

## [1.0.10] — 2026-05-26

First "ready for community testing" release. Adds wallet-balance
visibility, reorg detection, encrypted-at-rest credentials, and a test
suite so the previously-shipped behaviour can be refactored without
fear.

### Added

- **Wallet balance on the settings page** — shows spendable, awaiting
  confirmation, locked, and total GRIN at a glance, with a
  "last confirmed block N" footer. Falls back gracefully when the
  wallet is unreachable; the rest of the settings page still loads.
- **Chain-reorg detection** — the monitor service now re-checks
  recently-confirmed invoices (within the last 2 hours of `PaidAt`)
  and flips them back to `Broadcast` + fires `InvoiceInvalid` if the
  wallet reports the tx is no longer confirmed or confirmations have
  dropped below the store's threshold.
- **Credential encryption at rest** — `WalletPassword`, `ApiSecret`,
  and `WebhookSecret` columns now run through ASP.NET Core's
  `IDataProtector` before persistence. Legacy plaintext rows are read
  through as-is and re-encrypted lazily on next save. Key ring lives
  in BTCPay's existing DataProtection directory — see `SECURITY.md`
  for the durability story.
- **xunit test project** (`BTCPayServer.Plugins.Grin.Tests/`) with 23
  tests covering reorg detection, webhook signature determinism,
  encrypted-column round-trip / corruption / legacy passthrough, and
  invoice state-enum wire format. Run with `dotnet test`.

### Changed

- Bumped target framework to **net10.0** (BTCPayServer 2.3.9 dropped
  net8 support).
- Bumped BTCPay submodule to **v2.3.9**, EF Core / Npgsql to **10.0.x**,
  Roslyn analyzers to **5.3.0** to match.
- The two webhook-dispatch paths (`GrinService.DispatchWebhook` and
  `GrinPaymentMonitorService.DispatchWebhookAsync`) now both route
  signature computation through a shared `WebhookSignature.Compute()`
  helper. Eliminates a refactor-time risk where the two paths could
  drift on encoding.
- The reorg decision (`!walletConfirmed || confs < threshold`) is now
  a pure function in `ReorgDetection.IsReorged()` for unit-testability.

### Fixed

- `GrinService.DispatchWebhook` previously emitted
  `btcpay-sig: sha256=` with an empty value when the store had no
  `WebhookSecret` configured. Medusa rejects that as malformed; the
  header is now skipped entirely when no secret is set, matching the
  monitor-service path.

### Known issues / deferred

- **QR code on checkout is hard to scan** with mobile cameras (the
  slatepack is 500–1500 chars → version 30+ QR with ~1 px modules).
  Workaround: customers paste the slatepack manually into their
  wallet. Real fix (animated multi-frame QR via UR / BBQr) is on the
  roadmap for the next release.
- **Webhook delivery retries**: failed webhook dispatches log a
  warning and move on (no retry queue). If the consumer (e.g. Medusa)
  is unreachable for 30s+ around the confirmation event, the
  notification can be dropped. Operators monitoring high-value flows
  should keep an eye on plugin logs. Retry table is planned for the
  next release.
- **Unit-test coverage** at v1 is the decision logic + signature
  contract. Integration tests against a real grin-wallet are not yet
  in CI. Manual end-to-end is documented in `SETUP.md`.

## [1.0.9] — 2026-05-21

### Fixed

- Auto-redirect at the checkout now fires on `InvoiceProcessing`
  (broadcast → mempool), not just on `InvoicePaymentSettled` (full
  confirmation). Cuts the customer's wait at the "thank you" page
  from ~10 minutes to ~3 seconds.

## [1.0.8] — 2026-05-19

### Added

- Dispatch a webhook on payment broadcast (`InvoiceProcessing`) so
  downstream consumers can flip orders to "awaiting confirmation"
  immediately rather than waiting for full settlement.

## [1.0.7] — 2026-05-15

### Fixed

- Return-to-store button on the checkout-complete page.
- Auto-redirect on payment-complete when the customer is still on
  the checkout page.

## [1.0.6] — 2026-05-12

### Added

- USD price display on the checkout page (via Gate.io spot, cached
  2 minutes).
- `GrinRateProvider` registered with BTCPay's rate engine so GRIN
  pairs are available for rate rules and scripting.
- Webhook URL + secret fields in the store settings panel.
- Toggle to enable / disable Grin payments at the store level.

## [1.0.5] — 2026-04 / earlier

### Added

- Node API URL setting for detailed sync-status monitoring (sync
  percentage + phase visible in the BTCPay footer panel).
- Sync status indicator on the settings page.
- `SETUP.md` documenting the full grin-node + grin-wallet setup
  including Docker networking via socat proxies.

## [1.0.x] — earlier

Initial development releases — see commit history. Highlights:
- v3 encrypted Owner API client (ECDH + AES-256-GCM).
- Per-invoice slatepack issuance + finalization flow.
- Background payment monitor service polling at 30s.
- Per-store wallet configuration (URL, password, API secret).
- Invoice list with status badges on the settings page.
