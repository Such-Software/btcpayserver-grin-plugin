# Changelog

All notable changes to the BTCPay Server Grin plugin will be documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/). Patch-version-only releases
are skipped when they fix a single bug — see `git log` for the full history.

## [1.3.4] — 2026-06-19

### Fixed

- **Removed dead-code ceiling-rounding in IPaymentMethodHandler that
  caused every BTCPay invoice to render as "Processing (paid over)".**
  Earlier versions rounded the slate amount UP to 0.01 GRIN at
  issuance time so customers could type the amount into a mobile
  wallet keypad for the bare-address QR flow. That flow was removed
  (the slate_id-mismatch problem) but the rounding stayed as dead
  code. Result: slate signed for 40.98 GRIN, BTCPay tracking 40.974
  GRIN due, on-chain payment 40.98 → 0.006 GRIN over → flagged. For
  invoice-flow payments the customer's wallet pays exactly what the
  slate carries, no typing — so the rounding served no purpose, only
  introduced the overpayment artifact. Slates now issue at the
  exact `Calculate().Due` amount in nanogrin (the smallest indivisible
  unit), removing the artifact entirely.

## [1.3.3] — 2026-06-19

### Fixed

- **InvoiceWatcher could miss the recompute window when payment
  arrived between scheduled Wait() ticks.** Even with v1.3.2's
  `ReceivedPayment` event publish on the AddPayment-success path,
  an invoice registered under a previous plugin version (or whose
  `Wait()` watch lease elapsed before the payment landed) wouldn't
  ever re-evaluate. The dispatcher now also publishes
  `InvoiceNeedUpdateEvent(BtcpayInvoiceId)` on the
  "AddPayment-returned-null" path (i.e. payment already exists, no
  ReceivedPayment fires), which `InvoiceWatcher` subscribes to and
  uses to re-Watch the invoice. Recovers stuck invoices on the
  next dispatch (typically the Confirmed → Settled promotion).

## [1.3.2] — 2026-06-19

### Fixed

- **BTCPay invoice state stayed at `New` after Broadcast payment
  registered.** v1.3.1's two-phase bridge correctly called
  `PaymentService.AddPayment(Status=Processing)` on Broadcast, but
  the parent BTCPay invoice's status never transitioned out of
  `New` even with `paidAmount == amount`. Root cause: BTCPay's
  `InvoiceWatcher` only recomputes invoice state in response to
  the `InvoiceEvent.ReceivedPayment` event — the AddPayment call
  alone doesn't trigger it. Matches the canonical pattern at
  `LightningListener.cs:666` and `NBXplorerListener.cs:185`:
  publish the event manually after every successful AddPayment.
  `GrinSettlementDispatcher` now takes `EventAggregator` and
  publishes `ReceivedPayment` so the invoice promotes
  `New → Processing → Settled` correctly.

## [1.3.1] — 2026-06-18

### Added

- **Two-phase BTCPay payment registration.** The plugin now calls
  `PaymentService.AddPayment` twice across a payment's lifecycle:
  - On *Broadcast* (slatepack finalized, transaction posted to the
    network, 0 confirmations) — registers a `PaymentStatus.Processing`
    payment so the BTCPay invoice transitions out of its
    expiration-countdown window into the `Processing` state, the
    Grin logo appears on the Invoices list immediately, and the
    operator sees the payment is in-flight.
  - On *Confirmed* (≥ `MinConfirmations`, default 10) — promotes the
    existing payment row's `Status` to `Settled` via
    `UpdatePayments`, which fires the canonical
    `InvoiceEvent.PaymentSettled` event for downstream webhook
    consumers.

  Pre-1.3.1, AddPayment only fired on confirmation, so on the
  operator's Invoices list every in-flight Grin invoice showed as
  `Expired` (against the default 15-minute window) with no logo
  until 10 on-chain confirmations promoted it past the countdown.
  See [`DESIGN.md`](DESIGN.md) for the rationale.

- **`ICheckoutModelExtension` for `GRIN-CHAIN`.** Registers the
  Grin logo (`Resources/img/grin-logo.png`) so the checkout prompt,
  the Invoices list, and any other "what does this payment method
  look like?" surface in BTCPay renders the icon correctly.

- **JSON slatepack-submit endpoint** for external storefronts:
  `POST /stores/{storeId}/plugins/grin/invoices/{id}/submit`
  authenticated with the same `CanCreateInvoice` Greenfield scope as
  invoice creation. Accepts `{ "responseSlatepack": "..." }` and
  runs the same `decode → finalize → broadcast → enqueue webhook`
  pipeline as the form-based hosted checkout flow. Integrators that
  render their own Grin checkout UI (e.g. Medusa) can keep the
  customer on-domain instead of bouncing to the plugin's hosted
  view for the paste-response step.

- **`GET /stores/{storeId}/plugins/grin/invoices/{id}`** returns the
  full GrinInvoice record as JSON (slatepack address, slatepack
  message, amount, status, confirmations, `BtcpayInvoiceId`). Used
  by external storefronts that render their own checkout UI and
  need the slatepack data without scraping the hosted Checkout
  view.

- **`grin-wallet listen` documentation in [`SETUP.md`](SETUP.md)**
  including a companion systemd unit at
  [`contrib/systemd/grin-wallet-listen.service`](contrib/systemd/grin-wallet-listen.service).
  Operators were missing the second long-running grin-wallet process
  needed to register the Tor hidden service the slatepack address
  resolves to — without it, customers paying via `grin-wallet pay`
  hit an unreachable onion and the invoice times out.

### Changed

- **Amount rounded UP to 0.01 GRIN** at slate-issuance time. The
  `IPaymentMethodHandler` was previously taking BTCPay's
  rate-converted GRIN amount at full precision (e.g.
  `41.069352964 GRIN`) which is unusable to type into a mobile
  wallet keypad. Rounding to 2 decimals keeps the displayed amount
  typeable and the on-chain delta is ≤ 0.01 GRIN ≈ $0.00025 at
  current rates. Always rounds up so the merchant never undercharges.

- **Slatepack address removed from `TrackedDestinations`.** Every
  Grin invoice issued by the same wallet has the SAME slatepack
  address (the merchant's static wallet address — there are no
  per-invoice fresh addresses like BTC). Adding it to BTCPay's
  `AddressInvoice` table succeeded for the first invoice but threw
  a unique-constraint violation (`PK_AddressInvoices`) on every
  subsequent Greenfield-flow invoice with `GRIN-CHAIN`, returning
  500 to the caller. Tracking by `txSlateId` via
  `AdditionalSearchTerms` is preserved.

### Docs

- README.md: real install instructions for the BTCPay plugin
  directory; removed stale "23+ tests" claim and "Coming soon"
  marker. SECURITY.md: language polish for public release. SETUP.md:
  prerequisites now list `tor` (required by `grin-wallet listen` to
  spawn its embedded Tor process); version reference bumped to the
  current release; `GRIN_WALLET_PASS` env-var caveat documented for
  the `listen` subcommand.

- New [`DESIGN.md`](DESIGN.md) captures four load-bearing
  architectural decisions: why two grin-wallet processes
  (`owner_api` + `listen`), why we run grin-wallet's embedded Tor
  instead of sharing BTCPay's, why the plugin uses `IssueInvoiceTx`
  exclusively (vs sender-initiated sends, which produce a
  `tx_slate_id` we can't associate back to the original invoice),
  and why integrators get the JSON slatepack-submit endpoint.

## [1.3.0] — 2026-06-15

### Added

- **First-class BTCPay payment method (`GRIN-CHAIN`).** The plugin
  now implements `IPaymentMethodHandler<GrinPaymentMethodConfig>` and
  `IPaymentLinkExtension`, registering Grin as a payment method
  alongside Bitcoin / Lightning / LNURL. Stores can enable Grin from
  Store Settings → Payment Methods and integrators create invoices
  via the standard Greenfield API
  (`POST /api/v1/stores/{storeId}/invoices` with `paymentMethods:
  ["GRIN-CHAIN"]`). Settled payments flow through BTCPay's
  `PaymentService.AddPayment`, BTCPay's event aggregator fires the
  canonical `InvoicePaymentSettled` event, and the built-in
  `WebhookSender` delivers the standard webhook payload — same
  contract every other BTCPay payment method uses.

- **Persistent webhook delivery queue + retry worker** for the
  internal direct route. New `GrinWebhookDeliveries` table; a
  `GrinWebhookDeliveryWorker` HostedService ticks every 5s and
  retries failures on a 0s / 30s / 2m / 10m / 1h / 6h / 24h backoff
  schedule before dead-lettering. Each delivery captures payload at
  enqueue time, recomputes the HMAC signature per attempt against
  the live secret, and surfaces non-2xx responses + connection
  failures with structured fields (`LastResponseCode`, `LastError`,
  `AttemptCount`). Replaces the previous fire-and-forget POST.

- **Cross-process settlement guard.** A `SettlementWebhookSent`
  boolean column on `GrinInvoices` + a transactional
  `TryMarkSettlementWebhookSent` helper ensure the customer-side
  `/status` poll (5s) and the background monitor (30s) can't both
  fire the settlement webhook for the same confirmation event.
  Whoever wins the atomic UPDATE owns the dispatch; the other
  silently skips. Failed dispatches revert the guard so the
  monitor's `RetryUnsignaledSettlements` tick takes the next shot.

- **Greenfield-style API key auth on `POST /invoices`.** The
  internal direct route now requires
  `[Authorize(Policy = Policies.CanCreateInvoice,
  AuthenticationSchemes = AuthenticationSchemes.Greenfield)]` —
  matches the convention every other invoice-creating endpoint in
  BTCPay uses.

- **SSRF guard on operator-editable URLs.** Save-time validation on
  `WebhookUrl` / `NodeApiUrl` / `OwnerApiUrl` rejects non-http(s)
  schemes, URLs with embedded credentials, IMDS / cloud-metadata
  hosts (`169.254.169.254`, `metadata.google.internal`,
  `metadata.aws.internal`, `metadata.azure.com`, `fd00:ec2::254`),
  and URLs longer than 4096 chars. Loopback / RFC1918 / link-local
  hosts are warned but allowed (the standard docker-network
  topology has the wallet RPC on a private address).

- **GitHub Actions CI workflow** at `.github/workflows/dotnet.yml`.
  Build + test on push to master, pull requests, and manual
  dispatch. NuGet cache keyed by csproj hashes; concurrency cancels
  in-flight runs on the same branch.

- **HttpClient timeout (15s) on `GrinRPCClient`.** Was previously
  100s (.NET default), letting a stuck wallet block the monitor's
  30s tick for up to 100 seconds.

- **`[Authorize]` + `[DbContext]` attributes** on all migrations
  (required for EF Core discovery; an earlier migration that
  shipped without them only applied after the attributes were
  added).

### Changed

- README + SETUP describe the BTCPay-canonical setup as the primary
  (and only documented) integration path. The internal direct route
  at `POST /stores/{storeId}/plugins/grin/invoices` still exists
  for backward compatibility but is no longer mentioned in any
  user-facing docs — new integrations should use Greenfield.

- Webhook payloads through the internal queue route now include a
  `btcpay-grin-delivery-id` and `btcpay-grin-attempt` header so
  receivers can correlate retries to a single logical delivery.

- Test project's csproj now references `Microsoft.EntityFrameworkCore` +
  `Npgsql.EntityFrameworkCore.PostgreSQL` directly (the main plugin
  only ships them in non-Release builds, since BTCPay supplies the
  runtime at load time — but the test runner isn't BTCPay).

### Internal

- New `GrinSettlementDispatcher` service routes Confirmed events to
  either BTCPay's `PaymentService` (handler-created invoices) or
  the internal delivery queue (legacy direct route). One funnel
  shared by the monitor and the customer-side `/status` poll.

- `GrinService.DispatchWebhook` retired; all webhook emission
  routes through `GrinWebhookDeliveryService` + worker.

- Build-version dependency bump: `BTCPayServer >= 2.3.9`.

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
