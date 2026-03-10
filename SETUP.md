# Grin Plugin Setup Guide

This guide covers setting up the Grin node, wallet, and BTCPay Server plugin for accepting Grin payments.

## Prerequisites

- BTCPay Server v2.3.5+ (Docker or bare metal)
- A Linux server to run grin-wallet (can be the same machine as BTCPay or separate)

## Architecture

```
BTCPay Server (plugin)
    |
    | v3 encrypted JSON-RPC (ECDH + AES-256-GCM)
    |
grin-wallet (Owner API, port 3420)
    |
    | JSON-RPC
    |
Grin Node (API port 3413, P2P port 3414)
    |
    | P2P
    |
Grin Network
```

The plugin communicates exclusively with `grin-wallet` via its Owner API. The wallet connects to a Grin node (local or remote). The plugin never talks to the node directly.

### How the Encrypted API Works

The Owner API uses v3 encrypted JSON-RPC. Every session starts with an ECDH key exchange:

1. Plugin generates an ephemeral secp256k1 keypair
2. Plugin sends its public key to `init_secure_api`
3. Wallet returns its public key
4. Both sides derive a shared secret (ECDH x-coordinate)
5. All subsequent RPC calls are encrypted with AES-256-GCM using this shared key
6. Plugin calls `open_wallet` (encrypted) with the wallet password to get a session token
7. All further calls include the session token

This means even over plaintext HTTP, the RPC payload is encrypted end-to-end. The API secret (Basic Auth) protects against unauthorized access to the endpoint itself.

If the wallet restarts, the shared key becomes invalid. The plugin detects this (AES decryption failure) and automatically re-establishes the session.

## Step 1: Install Grin

Download grin and grin-wallet binaries from [grin.mw](https://grin.mw) or build from source.

```bash
# Install to /usr/local/bin (or anywhere on PATH)
sudo cp grin /usr/local/bin/
sudo cp grin-wallet /usr/local/bin/

# Create a dedicated user
sudo useradd -m -s /bin/bash grin
```

## Step 2: Set Up the Grin Node

You have two options: run your own node or use a remote node.

### Option A: Run Your Own Node (recommended for production)

```bash
# As the grin user
sudo -u grin bash
cd ~

# Initialize node config
grin server config

# Edit grin-server.toml:
#   - Set api_http_addr = "127.0.0.1:3413"
#   - Disable TUI: run_tui = false (for headless/systemd)

# Start the node (first sync takes several hours)
grin server run
```

Install as a systemd service (see [contrib/systemd/](contrib/systemd/)):

```bash
sudo cp contrib/systemd/grin-node.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now grin-node
```

Monitor sync progress:
```bash
curl -s http://127.0.0.1:3413/v2/owner \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"get_status","params":{}}' | python3 -m json.tool
```

The node is synced when `sync_status` is `"no_sync"` and `tip.height` matches the network height.

### Option B: Use a Remote Node

If you don't want to run your own node, you can point grin-wallet at a public node. Edit `grin-wallet.toml` after wallet init:

```toml
check_node_api_http_addr = "https://grincoin.org"
```

Known public nodes:
- `https://grincoin.org` (community node)
- `https://grinnode.live:3413` (community node)

Note: Using a remote node means trusting that node for transaction broadcasting and blockchain data. For maximum security, run your own node.

## Step 3: Set Up grin-wallet

```bash
# As the grin user
sudo -u grin bash
cd ~/.grin/main   # or wherever you want wallet data

# Initialize the wallet (you'll set a password)
grin-wallet init

# SAVE the recovery phrase securely
# SAVE the password — you'll need it for the BTCPay plugin config

# Note the API secret (generated automatically):
cat .owner_api_secret
```

### Configure the Wallet

Edit `~/.grin/main/grin-wallet.toml`:

```toml
[wallet]
# Owner API settings
owner_api_listen_port = 3420
owner_api_listen_interface = "127.0.0.1"  # or "0.0.0.0" if BTCPay is on another machine

# API secret for Basic Auth
api_secret_path = "/home/grin/.grin/main/.owner_api_secret"

# Node connection
check_node_api_http_addr = "http://127.0.0.1:3413"  # or remote node URL
```

### Start the Owner API

```bash
grin-wallet owner_api
```

Or install as a systemd service:
```bash
sudo cp contrib/systemd/grin-wallet.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now grin-wallet
```

### Verify the Wallet is Running

```bash
# Quick test — should return a JSON-RPC response
curl -s http://127.0.0.1:3420/v3/owner \
  -u "grin:$(cat /home/grin/.grin/main/.owner_api_secret)" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"init_secure_api","params":{"ecdh_pubkey":"02deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef"}}'
```

If you get a JSON response with `"result": {"Ok": "02..."}`, the wallet API is working.

## Step 4: Docker Networking (if BTCPay is in Docker)

If BTCPay Server runs in Docker and grin-wallet runs on the host, the container can't reach `127.0.0.1:3420` on the host. You need to expose the wallet API to the Docker bridge network.

### Option A: socat proxy (recommended)

Forward the Docker gateway IP to localhost:

```bash
# Find the Docker bridge gateway IP (usually 172.17.0.1 or 172.18.0.1)
docker network inspect bridge -f '{{range .IPAM.Config}}{{.Gateway}}{{end}}'

# Create a socat proxy
socat TCP-LISTEN:3420,fork,bind=172.18.0.1,reuseaddr TCP:127.0.0.1:3420
```

Install as a systemd service:

```ini
# /etc/systemd/system/grin-wallet-proxy.service
[Unit]
Description=Forward grin-wallet owner API to Docker network
After=grin-wallet.service

[Service]
Type=simple
ExecStart=/usr/bin/socat TCP-LISTEN:3420,fork,bind=172.18.0.1,reuseaddr TCP:127.0.0.1:3420
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

In BTCPay plugin settings, use `http://172.18.0.1:3420` as the Owner API URL.

### Option B: Bind wallet to 0.0.0.0

Set `owner_api_listen_interface = "0.0.0.0"` in grin-wallet.toml. The wallet will listen on all interfaces. Make sure port 3420 is not exposed to the internet (firewall it).

In BTCPay plugin settings, use `http://172.18.0.1:3420` (Docker gateway) or `http://<host-ip>:3420`.

## Step 5: Install the Plugin

### Option A: Upload via BTCPay UI

1. Download the latest `.btcpay` file from [GitHub Releases](https://github.com/Such-Software/btcpayserver-grin-plugin/releases)
2. Go to BTCPay **Server Settings > Plugins > Upload Plugin**
3. Upload the `.btcpay` file
4. BTCPay will restart and load the plugin

### Option B: Manual install (Docker)

If you have SSH access to the server, you can deploy directly to the plugin volume:

```bash
# Download and extract the release
cd /tmp
curl -sL https://github.com/Such-Software/btcpayserver-grin-plugin/releases/download/v1.0.3/1.0.3.0.tar.xz -o grin-plugin.tar.xz
tar xf grin-plugin.tar.xz

# Extract the .btcpay zip into the plugins volume
PLUGIN_DIR=/var/lib/docker/volumes/generated_btcpay_pluginsdir/_data/BTCPayServer.Plugins.Grin
mkdir -p "$PLUGIN_DIR"
python3 -c "import zipfile; zipfile.ZipFile('/tmp/1.0.3.0/BTCPayServer.Plugins.Grin.btcpay').extractall('$PLUGIN_DIR')"

# Restart BTCPay to load the plugin
docker restart generated_btcpayserver_1
```

### Option C: Build from source

See [README.md](README.md#manual--development) for build instructions using PluginPacker.

### Configure the Plugin

1. In BTCPay, go to your store's settings
2. Click **Grin** in the left sidebar
3. Enter:
   - **Owner API URL**: `http://127.0.0.1:3420` (or Docker gateway URL)
   - **Wallet Password**: the password you set during `grin-wallet init`
   - **API Secret**: contents of `.owner_api_secret`
4. Set **Minimum Confirmations** (default: 10, roughly 10 minutes)
5. Check **Enable Grin Payments** and click **Save**
6. Click **Test Connection** — should show the current node height

## Troubleshooting

### "Error opening wallet (is password correct?)"

The ECDH handshake succeeded (URL and API secret are correct) but `open_wallet` failed. The wallet password in plugin settings doesn't match the password used during `grin-wallet init`.

Fix: Re-enter the correct password in plugin settings, or re-initialize the wallet with the correct password:
```bash
sudo -u grin bash
cd ~/.grin/main
rm -rf wallet_data
grin-wallet init   # set the password you want
# Then update .owner_api_secret in BTCPay settings (it gets regenerated)
systemctl restart grin-wallet
```

### "Connection failed" / timeout

The plugin can't reach the wallet API at all.

- Check that grin-wallet owner_api is running: `systemctl status grin-wallet`
- Check the URL in plugin settings matches where the wallet is listening
- If using Docker, verify the socat proxy is running: `systemctl status grin-wallet-proxy`
- Test connectivity: `curl http://<url>:3420/v3/owner`

### Node height shows 0

The Grin node hasn't finished syncing. Initial sync can take several hours depending on your connection. Check sync status:
```bash
curl -s http://127.0.0.1:3413/v2/owner \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"get_status","params":{}}' | python3 -m json.tool
```

The wallet will work once `sync_status` shows `"no_sync"`.

### Payments not confirming

- Check that the Grin node is synced (see above)
- The background monitor polls every 30 seconds — confirmations update automatically
- Minimum confirmations default is 10 (~10 minutes)
- Check BTCPay logs: `docker logs generated_btcpayserver_1 --tail 100 | grep -i grin`

## Security Considerations

- **Wallet credentials** (password, API secret) are stored in BTCPay's PostgreSQL database
- **The Owner API uses encrypted RPC** — even over HTTP, payloads are AES-256-GCM encrypted
- **The plugin never holds private keys** — all signing happens in grin-wallet
- **Bind the Owner API to localhost** (`127.0.0.1`) and use socat for Docker access
- **Don't expose port 3420 to the internet** — the API has full wallet control
- **Self-hosted BTCPay = self-custodial**. Third-party BTCPay = you're trusting the operator with wallet credentials (same trust model as Lightning in BTCPay)
