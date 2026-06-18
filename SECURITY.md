# Security Policy

This document covers reporting vulnerabilities, what data the plugin
stores and how, and operational practices that materially affect the
security posture of a Grin-accepting BTCPay store.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security reports.**

Email security disclosures to **security@such.software** with:

- A description of the issue.
- Steps to reproduce (or a minimal proof of concept).
- Your assessment of impact.

We aim to acknowledge within **3 business days** and ship a fix or
documented mitigation within **30 days** for confirmed vulnerabilities.
For severe issues (remote code execution, wallet credential
exfiltration, transaction-level attacks against merchants), expect
much faster turnaround.

Reporters who request credit will be named in the fix's release notes.

## Trust model in one paragraph

The Grin plugin is **self-custodial in the BTCPay sense**: the
merchant runs their own `grin-wallet`, the wallet holds the private
keys, and the plugin only ever sees enough state to construct
slatepack exchanges and check confirmations. The plugin does **not**
hold seed phrases, output secrets, or any keying material that would
let it spend merchant funds independently. What it does hold is the
wallet's *Owner API* credentials (URL + password + API secret),
which an attacker could use to drain the wallet — see "What's stored
in the database" below.

## What's stored in the database

| Column                                     | Sensitivity | Encrypted at rest? |
|--------------------------------------------|-------------|--------------------|
| `GrinStoreSettings.OwnerApiUrl`            | Low (URL)   | No (plaintext)     |
| `GrinStoreSettings.NodeApiUrl`             | Low (URL)   | No                 |
| `GrinStoreSettings.WalletPassword`         | **High**    | **Yes** (v1.0.10+) |
| `GrinStoreSettings.ApiSecret`              | **High**    | **Yes** (v1.0.10+) |
| `GrinStoreSettings.WebhookSecret`          | Medium      | **Yes** (v1.0.10+) |
| `GrinStoreSettings.MinConfirmations`       | None        | No                 |
| `GrinStoreSettings.Enabled`                | None        | No                 |
| `GrinInvoice.*`                            | Low         | No                 |

The three sensitive fields are protected via ASP.NET Core's
`IDataProtector` (the same mechanism BTCPayServer uses for its
Lightning connection strings). Encrypted values land in the `text`
column as `enc:v1:<base64-blob>`. Rows written before v1.0.10 stay as
plaintext and re-encrypt lazily on their next save — you can force
re-encryption by clicking "Save" on each store's Grin settings panel
after upgrading.

## Operational requirements

### Persist the BTCPay DataProtection key ring

The encryption above is only useful if the key ring is persistent.
ASP.NET Core's default key-persistence location depends on how
BTCPayServer is deployed:

- **BTCPay Docker (`btcpay-docker`)** — keys go to
  `/datadir/Data/DataProtection-Keys` inside the container, which is
  mapped to a host volume by the default compose file. Don't delete
  this volume.
- **Bare-metal BTCPay** — keys go to `$HOME/.aspnet/DataProtection-Keys`
  for whatever user runs the BTCPay service.

If the key ring is lost or rotated without retention, **every
encrypted column becomes unrecoverable**. The plugin handles this
gracefully (decryption failure → empty string → settings page prompts
operator to re-paste credentials) but it's an avoidable outage.
**Back up your DataProtection-Keys directory** alongside your Postgres
backups.

### Network exposure of `grin-wallet`'s Owner API

The Owner API by default listens on `127.0.0.1:3420` and the plugin
talks to it via v3 encrypted JSON-RPC (ECDH key exchange +
AES-256-GCM). Do **not** expose the Owner API on a public interface
— the v3 encryption protects the wire but not against denial-of-
service or session-handshake replay. If you need to reach the wallet
from a remote BTCPay (e.g. Docker on a different host), put it behind
a TLS-terminating reverse proxy or use a socat / WireGuard tunnel.
See `SETUP.md`'s "Docker Networking" section for the standard
pattern.

### Grin node

You can run your own node or point the wallet at a public one. Public
nodes don't see anything sensitive — Grin transactions are private by
design — but they can rate-limit you or go offline, both of which
cause confirmations to stall. For a production store, run your own
node.

### Webhook delivery

Webhooks are signed with HMAC-SHA256 using the per-store
`WebhookSecret` and an HTTP header named `btcpay-sig`. The wire
format is:

```
btcpay-sig: sha256=<lowercase-hex>
```

Webhook consumers must verify this signature before acting on the
payload. Failure modes worth knowing:

- If you change `WebhookSecret` after invoices exist, those invoices'
  subsequent webhook deliveries will use the new secret. Re-distribute
  to your consumer's config before changing.
- Failed delivery (5xx from the consumer, network timeout, etc.) is
  currently logged-and-dropped — there's no retry queue. If you are
  building anything money-critical on top of these webhooks, monitor
  the plugin logs. Retry-on-failure is on the roadmap for the next
  release.

## Known security gaps

These are tracked openly so adopters can make informed decisions:

- **No webhook delivery retry queue.** Listed in `CHANGELOG.md`
  "Known issues." Until shipped, treat webhook deliveries as
  "at-most-once."
- **No request-rate limiting on the public checkout endpoint** beyond
  what BTCPayServer's own middleware provides. A flood of `POST`
  requests with garbage slatepacks would consume RPC cycles against
  `grin-wallet`. Mitigation: rate-limit the BTCPay reverse proxy
  upstream of the plugin.
- **Slatepack input has no max-length cap** at the controller level
  (relies on ASP.NET's default request body limit). A 10MB string of
  garbage would attempt slatepack decoding before being rejected.
  Low impact (decoding fails fast) but worth a defense-in-depth cap;
  tracked as a follow-up.

If you spot a gap not listed here, the disclosure process at the top
of this file is the right path.
