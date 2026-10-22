# Shared-printer customizations

Based on Fewiel/SpoolHero v2.4.2, commit a03038a6c1ab3d41d5661a7000f6879e0e873a65. The upstream AGPL-3.0 license applies.

## Start with Docker

Clone https://github.com/Kaskobiii/SpoolHero.git and enter the SpoolHero directory. Set `MYSQL_ROOT_PASSWORD` and `JWT_SECRET_KEY` in `.env` beside `docker-compose.yml`, as described in the README. Then run:

```sh
docker compose up -d --build
```

The Dockerfile uses stable .NET 10 SDK and runtime images. The app is available on port 5000.

## Upgrading an existing installation

Keep the existing Compose project name, database volume, and credentials. Back up your database before deploying an upgrade containing a schema migration. Update the source and rebuild the app:

```sh
docker compose up -d --build app
```

Migration 18 makes inventory text fully Unicode compatible while preserving IDs and relationships. Migration 17 adds project-scoped owners and an optional owner reference on spools. It runs automatically at startup. Existing spools remain unassigned until you choose an owner. Notes are preserved; owner names are not inferred from notes.

## Owners

Use **Spools & Material → Owners** on desktop or mobile to add owners or rename them. Owners belong to the selected project and do not need user accounts. Members of that project can manage them.

Select **Owner** in the spool editor. Choose **Unassigned** to clear ownership. Copying a spool keeps its owner. The main Spools page groups by owner name with Unassigned last; existing search and active/consumed filters still apply. The dashboard, detail page, and location overview show ownership too. The home dashboard keeps its existing six-recent-spool limit.

## Material colors

The palette button in a material row opens **Add color variant**. Shared properties such as brand, type, temperatures, density, diameter, weight, drying settings, price, notes, and reorder link are copied; you choose the new color. You can adjust any property for an individual variant. New variants get their own material IDs and are created in the selected project.

When creating a material or variant, **Save & add another color** saves it and retains the shared settings for the next color. The normal Save button returns to the material list.

## Favorites and catalog settings

Favorites are filtered on the server before counting and pagination. Other material filters still apply. Removing the last favorite on a page moves back to the last populated page.

The OpenFilamentDB catalog is enabled by default. In **Admin → Settings → General**, disable **Show OpenFilamentDB materials** to show only materials created in each project. This applies to material lists, searches, filter choices, favorites, pickers, and exports, and disables catalog syncing. This is an installation-wide setting saved in the database. Materials used by existing spools are retained. Re-enable the setting to restore catalog visibility.

## Full inventory backup and transfer

Open **Backup & transfer** from your account menu, or **Export all** from the Spools page. The JSON backup contains all projects you administer and their materials, spools, owners, printers, storage, dryers, photos, lifecycle dates, NFC IDs and your relevant material favorites. It includes referenced or favorited global materials, without copying the entire unused catalog.

On a new Docker installation, sign in as the platform administrator, open **Backup & transfer** before creating inventory or syncing the catalog, select the saved file, inspect its counts, and click **Import backup**. Optionally restore the OpenFilamentDB preference. Imported projects belong to the importing account. Conflicting inventory IDs reject the entire import without overwriting data.

Accounts/passwords, memberships/invitations, other users' private favorites, audit/support records and server/email secrets stay with each installation. See [docs/BACKUP.md](docs/BACKUP.md) for the complete procedure and limits.

## Verification

The Release server/client build and publish passed using .NET SDK 10.0.402. Migration 17 and API behavior were checked against an isolated MariaDB 11.8 database. Docker itself is not installed in the verification environment, so an image build/container launch was not performed.

Run the color-form regression checks with .NET 10:

```sh
dotnet run --project tests/MaterialColorFlowTests -c Release
```

The three Python smoke scripts use only the standard library and require a disposable running instance. Set `SPOOLHERO_TEST_URL`, `SPOOLHERO_TEST_EMAIL`, and `SPOOLHERO_TEST_PASSWORD` to its address and admin credentials, then run:

```sh
python3 tests/owner_management_smoke.py
python3 tests/material_favorites_smoke.py
python3 tests/material_catalog_smoke.py
```

These scripts create uniquely named projects and sample data for inspection. The catalog test restores the previous setting. The tests cover owner validation/renaming/assignment/project isolation, favorites across more than 100 materials, and catalog visibility/authorization/persistence/existing spool references. The color-form checks cover copied properties, separate material identities, repeated saves, failures, and transitions between edit/new/variant routes.

The upstream build emits dependency vulnerability warnings and an existing unawaited-call warning. Those dependency upgrades are outside this customization.

A two-instance transfer test is available in `tests/inventory_backup_smoke.py`. Run it only against two disposable instances using the source/destination URL and credential variables described at its top. It checks complete inventory roundtrips, Unicode, images, retained NFC IDs, collision protection, malformed files, and transaction rollback. Optional explicitly named test databases enable real mid-restore failure injection.
