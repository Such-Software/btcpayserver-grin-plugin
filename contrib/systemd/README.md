# Systemd Service Templates

These are example systemd unit files for running a Grin node and wallet as background services.

## Setup

1. Create a dedicated user:
   ```bash
   sudo useradd -m -s /bin/bash grin
   ```

2. Install grin and grin-wallet binaries to `/usr/local/bin/` (or adjust `ExecStart` paths).

3. Initialize the wallet (as the grin user):
   ```bash
   sudo -u grin grin-wallet init
   ```

4. Copy the service files:
   ```bash
   sudo cp grin-node.service /etc/systemd/system/
   sudo cp grin-wallet.service /etc/systemd/system/
   sudo systemctl daemon-reload
   ```

5. Enable and start:
   ```bash
   sudo systemctl enable --now grin-node
   # Wait for node to sync, then:
   sudo systemctl enable --now grin-wallet
   ```

6. Check status:
   ```bash
   systemctl status grin-node
   systemctl status grin-wallet
   journalctl -u grin-wallet -f
   ```

## Notes

- The wallet service depends on the node service (`After=grin-node.service`)
- Default data directories: `~grin/.grin/` (node) and `~grin/.grin-wallet/` (wallet, varies by version)
- The Owner API listens on `127.0.0.1:3420` by default — only accessible locally
- The API secret file is at `~grin/.grin-wallet/main/.owner_api_secret` (or similar)
- Adjust `User`, `Group`, and `WorkingDirectory` if your setup differs
- For remote node setups, configure `grin-wallet.toml` to point at the node's API address
