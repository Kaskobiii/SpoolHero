# Backup and transfer

Open **Settings → Backup & transfer**, or **Export all** at the top of the Spools page. The same tools are available in the mobile menu.

**Export all** downloads one JSON file containing every project's materials, spools, owners, printers, storage locations, dryers, photos and shared favorites. Notes, net grams, original filament and empty reel weights, lifecycle dates, inventory GUIDs, NFC UIDs and stable numeric printer IDs travel with it. The next unused printer ID and fractional usage are preserved too.

## Move to Docker

1. Export the local inventory and keep the JSON file.
2. Start a fresh Docker installation of this fork and open its web page. No sign-in is needed.
3. Open **Backup & transfer** and choose the saved file.
4. Check the counts, optionally restore the OpenFilamentDB preference, then click **Import backup**.
5. Click **Open imported inventory** and verify your spools before retiring the local installation.
6. Create a new private address in **Locations → Printers → select your printer → Printer connection** and update the printer's Spoolman URL when the printer is ready.

Imports create the saved projects and preserve their IDs and relationships. Conflicting inventory IDs reject the entire import without overwriting data. A fresh installation is the easiest destination. A rejected import leaves the inventory unchanged.

The backup excludes old account/password records, invitations, audit/support logs, printer connection secrets and email/server settings. All imported projects are shared with everyone using the local installation. Earlier version 1 inventory backups remain supported.

Files can be up to 64 MB. Keep inventory backups somewhere you control. This export is separate from backing up the Docker database volume and server configuration.
