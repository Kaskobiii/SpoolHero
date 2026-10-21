# SpoolHero

SpoolHero is a self-hosted filament management tool for 3D printing. It allows you to track spools, materials, storage locations, and drying cycles across multiple printers and projects. NFC tags (OpenSpool standard) can be written and read directly from the browser on Android.

## Custom features in this fork

- **Owners:** Open **Spools & Material → Owners** to add or rename the people sharing a printer. Select **Owner** when adding or editing a spool. The Spools page groups spools alphabetically by owner, followed by **Unassigned**. Renaming an owner updates all their spools. Existing spools start unassigned; notes are retained and displayed separately.
- **Color variants:** Use the palette action on a material, or **Add color variant** in its editor, to copy its settings into a new color. **Save & add another color** keeps those settings for the next color. Each color is a separate material for tracking.
- **Favorites:** The favorites filter applies before pagination, so favorites from every material page appear with accurate totals.
- **Project-only materials:** Disable **Show OpenFilamentDB materials** in **Admin → Settings → General** to hide the global catalog in material lists and pickers. The setting persists across restarts.

See [CUSTOMIZATIONS.md](CUSTOMIZATIONS.md) for upgrades and verification.

## Features

- Spool tracking with weight, color, material, and location
- NFC tag support via OpenSpool standard (NTAG215/216)
- Multi-user projects with invitation codes
- Shared material database with import/export
- Dryer monitoring with notifications
- Inventory scanning via NFC
- Admin panel with branding, SMTP, user management, and support tickets
- Email notifications for spool levels, dryer cycles, and ticket replies
- Self-hostable, open source, no external dependencies

## Quick Start with Docker

The easiest way to run SpoolHero is with Docker Compose:

```bash
git clone https://github.com/Kaskobiii/SpoolHero.git
cd SpoolHero
docker compose up -d --build
```

SpoolHero will be available at `http://localhost:5000`. A MySQL 8 database is included and configured automatically.

**Default admin account:**
- Email: `admin@localhost`
- Password: `admin`

Change the password immediately after first login.

### Configuration

Set environment variables in a `.env` file next to `docker-compose.yml`:

```env
MYSQL_ROOT_PASSWORD=YourSecureDatabasePassword
JWT_SECRET_KEY=YourRandomStringAtLeast32Characters
```

The OpenFilamentDB catalog is enabled by default. To use only materials created in your own project, open **Admin → Settings → General** and turn off **Show OpenFilamentDB materials**. This setting is saved in the database and takes effect in material lists, searches, favorites, and spool material pickers. It also disables catalog syncing. Existing spools keep their material information, and turning the catalog back on restores the global materials without another import.

### HTTPS (required for NFC on Android)

SpoolHero supports NFC tag reading and writing via the Web NFC API. **Web NFC only works over HTTPS** (except on localhost). If you want to use NFC features from your phone, you must put a reverse proxy with SSL in front of SpoolHero.

Example with **nginx** and Let's Encrypt:

```nginx
server {
    listen 443 ssl;
    server_name spoolhero.example.com;

    ssl_certificate /etc/letsencrypt/live/spoolhero.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/spoolhero.example.com/privkey.pem;

    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Alternatively, you can use [Caddy](https://caddyserver.com/) which handles SSL certificates automatically:

```
spoolhero.example.com {
    reverse_proxy localhost:5000
}
```

## Manual Setup (without Docker)

### Requirements

- .NET 10.0 Runtime
- MySQL 8.0 or MariaDB 10.6+
- A reverse proxy (Apache or nginx recommended)

### Installation

Download the latest release zip from the [Releases](https://github.com/Fewiel/SpoolHero/releases) page and extract it to your server. Then create an `appsettings.json` based on the included `appsettings.example.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Port=3306;Database=SpoolManager;User=spoolmanager;Password=YOUR_DB_PASSWORD;"
  },
  "Jwt": {
    "SecretKey": "at-least-32-characters-random-string"
  }
}
```

The database schema is created automatically on first start via FluentMigrator.

A default admin account is created on first start:
- Email: `admin@localhost`
- Password: `admin`

Change the password immediately after first login.

For a full installation guide including Apache reverse proxy and Let's Encrypt, see [INSTALL.md](INSTALL.md).

## Contributing

Pull requests are welcome. Please keep changes focused and describe what you changed and why.

For larger features, open an issue first to discuss the approach.

## Roadmap

- Klipper integration (read/write spool data directly from Klipper via Moonraker API)

## License

GNU Affero General Public License v3.0 (AGPL-3.0)

Free to use, modify, and distribute. Any modifications — including running a modified version as a hosted service — must be made available under the same license. See [LICENSE](LICENSE) for details.
