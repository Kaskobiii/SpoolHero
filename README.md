# SpoolHero — shared local inventory

A local-network fork of [Fewiel/SpoolHero](https://github.com/Fewiel/SpoolHero), customized for two people sharing a Snapmaker U1.

Open the app and use the inventory immediately. There is **no login, registration, password, account management or session expiry**. Everyone who can reach this installation can manage all projects, materials, spools and settings. Favorites are shared. Use this version on your trusted local network.

## Features

- Owners with editable names; spool lists grouped by owner.
- Remaining filament in grams, separate original filament and optional empty reel weights, plus a scale-weight calculator.
- Touch-friendly location and owner buttons with selected checkmarks.
- Color variant creation without re-entering the material properties.
- Complete favorite filtering and an optional project-material-only catalog.
- NFC/OpenSpool tag tools, printer/storage/dryer organization, notes and lifecycle dates.
- Full inventory export/import, preserving GUIDs, owners, photos, NFC UIDs and printer IDs.
- An opt-in Spoolman-compatible connection for paxx12 U1 SpoolLink and Moonraker usage reports. See [printer setup](docs/SPOOLMAN.md).

## Run with Docker

Install Docker with Compose, clone this fork, and run:

```sh
git clone https://github.com/Kaskobiii/SpoolHero.git
cd SpoolHero
```

Start the published fork image with:

```sh
bash scripts/install.sh
```

Open `http://<server-ip>:5000`, then import your temporary app's **Export all** file under **Settings → Backup & transfer**. Update later with:

```sh
bash scripts/update.sh
```

The update command backs up the database and replaces the app container while retaining saved inventory. See the [Docker installation and update guide](docs/DOCKER.md), including the one-time GitHub Actions/package visibility setup and recovery instructions.

## Upgrade an existing installation

Back up the database volume before rebuilding. Existing inventory IDs, owner assignments, quantities and notes are retained. The original inventory identity is kept internally so favorites and relationships survive; old accounts are not exposed as sign-in options. Previously separate projects become available to everyone using this installation. Existing favorites are merged once into the shared collection.

Old `/login` bookmarks redirect to the dashboard. The login, password, user-management and invitation APIs have been removed. The printer connection keeps its own private URL; an existing connection continues to work after an upgrade.

## Development and verification

Build with .NET 10 and MySQL/MariaDB. Set `ConnectionStrings__Default` for the database and run the server project. See [customizations](CUSTOMIZATIONS.md) for implementation details.

The credential-free regression is `tests/shared_inventory_smoke.py`. It uses two disposable servers, checks legacy data preservation, shared favorites/CRUD/settings, removed account endpoints, browser-origin checks, printer WebSockets/usage and backup restore/collision handling. Tests for earlier authenticated versions are retained as historical fixtures; their login/permission expectations do not apply to this shared installation.

This fork retains upstream's license and attribution. See [LICENSE](LICENSE).
