# Docker installation and updates

This guide is for Kaskobiii/SpoolHero, the shared local-network fork. No app login is required. Anyone who can reach port 5000 can manage the inventory. Keep it on your trusted LAN; do not forward it to the internet.

## One-time image publishing setup

The fork's **Publish fork Docker image** GitHub Actions workflow builds each main-branch update. It starts a disposable MySQL installation, creates a spool, replaces the app container, and verifies the saved spool before publishing. The image supports Intel/AMD (`amd64`) and ARM64 machines. ARM64 is built but the runtime smoke test runs on amd64.

1. On the fork's **Actions** tab, enable workflows if GitHub asks. After enabling, select **Publish fork Docker image → Run workflow → main** if no build is running.
2. Wait for a green workflow run.
3. On the GitHub profile's **Packages** page, open the **spoolhero** container package → **Package settings → Change visibility → Public**. New packages initially default to private. This lets your friend download the image without a GitHub token. The source code is already public; inventory is never included in this image.

Image: `ghcr.io/kaskobiii/spoolhero:latest`. Each build also has an immutable-by-convention commit tag `sha-<full-commit-id>` for selecting a specific build.

## Install on your friend's machine

Install Docker Engine/Desktop with a current Docker Compose v2 and Git. On Linux, macOS, or Windows using WSL/Git Bash:

```sh
git clone https://github.com/Kaskobiii/SpoolHero.git
cd SpoolHero
bash scripts/install.sh
```

The script creates `.env` with a random internal database password and starts the published image. Keep the folder and `.env`. Open `http://<server-LAN-IP>:5000`. To change the port, edit `SPOOLHERO_PORT` in `.env` before installation. Allow this port through the host's LAN firewall.

On the temporary app use **Settings → Backup & transfer → Export all**. Import that JSON file on the Docker installation. After import choose the imported project, then generate a new private printer address using the server's LAN IP. Enter it in the U1's Spoolman Integration settings.

All inventory, photos, branding and printer mappings are stored in MySQL's named `mysql_data` volume. Replacing the app container preserves this volume. The database has no published host port. No separate uploaded-file volume is required by this fork.

## Update from SpoolHero

Fresh installs include a private updater service. Open **Settings → Updates**, check for updates, then select **Update now**. Existing checkouts run `git pull --ff-only` and `bash scripts/enable-updater.sh` once to enable this. The service has Docker control inside the LXC and no published port.

For Android NFC see [private local HTTPS](LOCAL_HTTPS.md).

## Terminal update fallback

In the same SpoolHero folder:

```sh
bash scripts/update.sh
```

The command downloads the current `latest` app image, briefly stops the app to pause writes, saves a complete compressed database backup and local configuration under `backups/`, then replaces only the app container and waits for health checks. It keeps the database container, volume and printer address. Avoid updating during a print: usage reporting needs the app available.

A download or backup failure leaves/restarts the existing app. If the new app fails its health check, the script reports failure and retains the backup for recovery. It never silently claims success. Each backup contains private inventory and configuration, so store it privately and copy important backups to another machine. Old backups are retained; remove ones you no longer need to manage disk space.

Updates do not run automatically. Saving code to the fork builds an image; your friend chooses when to run the command. There is no need to pull source changes for ordinary image updates. If a future update changes the deployment scripts/Compose configuration, review those instructions and update the checkout first.

To select a specific build, change `SPOOLHERO_IMAGE` in `.env` to `ghcr.io/kaskobiii/spoolhero:sha-<full-commit-id>` and run the update script. Return to `:latest` to follow new builds.

## Recovery and existing installations

Never run `docker compose down -v`, delete the database volume, rename the installation folder, or change the Compose project name on a live installation. A different project name can create a fresh empty volume. Keep the existing `.env` password when upgrading an older source-built installation; changing this variable does not change an existing MySQL password. The original `mysql_data` volume key is preserved.

If an update fails, inspect `docker compose logs app`. The backup directory includes `previous-image.txt` with the old image ID and `database.sql.gz` with the pre-update database. A new version may have changed the database schema: do not simply run an older image against the migrated database. For recovery stop the app, restore the SQL backup into a **fresh, separate database volume**, use the matching saved Compose/environment configuration and previous app image, and verify the restored inventory before switching over. Leave the original volume intact until recovery is confirmed. Ask for help with the specific failure before restoring on a live server.

For routine transfers the app's Export all/Import feature remains available. The update script's SQL snapshots are full database backups, not JSON files for the app's Import button.

If image download says `denied` or `unauthorized`, confirm the package visibility is Public and the first Actions build succeeded. For private packages, the machine needs `docker login ghcr.io` with an account/token authorized to read that package. No registry credential is needed for a public package.

If registry publishing is unavailable, source builds remain supported: `docker compose build app`, followed by `docker compose up -d --no-build --pull never --wait`. Export all first. This fallback is separate from the normal registry update script.
