# Contributing

Thanks for considering a contribution. This plugin is in
"community testing" — production-ready for early adopters but with a
short list of known gaps tracked in [CHANGELOG.md](CHANGELOG.md).
PRs that close those gaps or harden the existing surface are the
most valuable.

## Reporting bugs

Open a GitHub issue with:

- BTCPayServer version and deployment style (Docker / bare-metal).
- Plugin version (visible at the top of the Grin settings page).
- Grin node + wallet versions.
- Steps to reproduce.
- Relevant log lines from BTCPay's `pm2 logs` / journalctl output.
  Plugin log entries are prefixed `BTCPayServer.Plugins.Grin.*`.

Do NOT include wallet passwords, API secrets, slatepacks, or other
sensitive material — redact before pasting.

## Security issues

Email security@such.software directly. Do not open a public issue.
See [SECURITY.md](SECURITY.md) for the full process.

## Requesting features

Open a GitHub issue tagged `enhancement`. Even better is a PR that
implements the change — but discuss the design first if it touches
the database schema, the webhook wire format, or the slatepack
exchange flow.

## Setting up a local development environment

1. Install prerequisites:
   - **.NET 10 SDK** (`sudo apt install dotnet-sdk-10.0` on Ubuntu
     24.04, or grab the installer from https://dot.net).
   - PostgreSQL (any 12+).
   - A Grin node + wallet — see [SETUP.md](SETUP.md) for the full
     guide.
2. Clone the repo with submodules:

   ```bash
   git clone --recurse-submodules https://github.com/Such-Software/btcpayserver-grin-plugin.git
   cd btcpayserver-grin-plugin
   ```

3. Build:

   ```bash
   dotnet build BTCPayServer.Plugins.Grin/BTCPayServer.Plugins.Grin.csproj
   ```

4. Run BTCPayServer with the plugin loaded:

   ```bash
   export BTCPAY_DEBUG_PLUGINS="$(pwd)/BTCPayServer.Plugins.Grin/bin/Debug/net10.0/BTCPayServer.Plugins.Grin.dll"
   dotnet run --project btcpayserver/BTCPayServer --no-launch-profile
   ```

   See `SETUP.md` for the full set of BTCPay env vars to configure.

## Running tests

```bash
dotnet test BTCPayServer.Plugins.Grin.Tests/BTCPayServer.Plugins.Grin.Tests.csproj
```

The test suite is fast (<1s) and covers reorg-detection logic, the
webhook signature contract, encrypted-column round-trip, and the
invoice state-enum wire format. **PRs must keep all tests green.**

If you're adding behaviour, add tests:

- Pure decision logic (e.g. a new state-transition rule) → add to an
  existing `*Tests.cs` or create a new one in
  `BTCPayServer.Plugins.Grin.Tests/`.
- New webhook events → extend `InvoiceStateTests.WebhookEventNames_StayStable`.
- Anything touching `GrinPaymentMonitorService` → extract the
  decision into a pure helper (like `ReorgDetection.IsReorged`) and
  test the helper rather than mocking the whole RPC stack.

## Code style

- **C# conventions** — follow the existing files. Async-all-the-way,
  early-return guard clauses over nested `if`s, explicit
  `using var` for IDisposable scope.
- **Comments** — explain the *why*, not the *what*. Anything
  surprising (a workaround, a security trade-off, an edge case from
  Grin's RPC) deserves a sentence; routine code does not.
- **Encoding-sensitive paths** (HMAC, slatepack base64, hex
  representations) — always specify the encoding explicitly. Drift
  on this class of code is the #1 source of integration outages.
- **Logging** — `_logger.Log{Info,Warning,Error}` with structured
  parameters (`"invoice {InvoiceId}"`, not `$"invoice {invoice.Id}"`)
  so log aggregators can filter cleanly.
- **Secrets** — `WalletPassword`, `ApiSecret`, `WebhookSecret` and
  anything new of similar shape must go through the
  `EncryptedColumnConverter`. Never log secrets in plaintext, even
  at Debug level.

## Commit messages

Conventional Commits style:

```
type: short imperative summary

Optional longer body explaining the WHY. Wrap at 72 columns.
```

`type` is one of `fix`, `feat`, `chore`, `docs`, `test`, `refactor`.
Reference issue numbers when relevant (e.g. `fix #42: ...`).

## Pull requests

- One logical change per PR. Bundling four unrelated fixes in one
  branch makes review slower for everyone.
- Update `CHANGELOG.md` under the "Unreleased" section (or the
  current in-progress version) with a one-line entry.
- If your change touches the wire format (webhook payload, RPC
  shape, database schema), call that out explicitly in the PR
  description and bump the plugin version under
  `BTCPayServer.Plugins.Grin.csproj`.
- CI must pass: `dotnet build` + `dotnet test`. Local equivalent:

  ```bash
  dotnet build && dotnet test
  ```

## Project roadmap

See `CHANGELOG.md` "Known issues / deferred" — the planned roadmap
for the next release is:

- Animated multi-frame QR codes (UR / BBQr) for slatepack delivery
  so mobile cameras can scan them.
- Webhook delivery retry queue with exponential backoff.
- Tor / `.onion` slatepack address support (so customers can pay
  without manually pasting).
- Multiple wallets per store (for high-volume merchants who shard
  output sets).
- Integration tests against a real Grin testnet wallet, gated on
  a CI secret.

If you want to take ownership of one of these, leave a comment on
the relevant issue first.

## License

By contributing you agree your changes are released under the
project's MIT license. See [LICENSE](LICENSE).
