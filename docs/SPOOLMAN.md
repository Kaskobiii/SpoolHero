# Snapmaker U1 / Spoolman connection

SpoolHero can provide the subset of the Spoolman API used by paxx12 Extended
Firmware v1.6's SpoolLink and Moonraker. It connects existing SpoolHero spools
to the printer's native spool selection and consumption reporting. It does
not run a second inventory or a separate Spoolman database.

## Set up

1. Set a positive **density** (g/cm³) and **diameter** (mm) on each material you
   intend to print. These values convert the printer's reported filament
   length to estimated grams. Missing values block length consumption with
   an actionable error; SpoolHero does not guess a material density.
2. In the selected SpoolHero project, open **Locations → Printers → Printer connection** and enable the connection. Copy the complete private URL.
   It is displayed once. Generating a replacement URL revokes the old one.
3. Deploy SpoolHero to a machine reachable from the printer. Use its LAN
   hostname or IP address and published Docker port in the URL; `localhost`
   refers to the printer itself when entered on the printer. The temporary
   local instance bound only to `127.0.0.1` is for entering inventory and
   testing, and cannot be reached by a separate printer.
4. In paxx12's firmware-config, open **Settings → Snapmaker Components →
   Spoolman Integration**, enable it, and enter that URL as the Spoolman
   server address. Keep the private path in the URL. Apply the configuration
   and restart the firmware components as directed by the printer UI.
5. Link each NFC card UID to the intended spool once. Existing valid hex
   **RFID tag UID** values in SpoolHero are available immediately. For a new
   tag, load it on a channel and use paxx12's `SET_SPOOL_ID LANE=E0 SPOOL_ID=5`
   command (replace channel and numeric ID with those shown in the integration
   page). The firmware reads the UID and binds it through the API. The
   SpoolHero `_sm_spool_id` GUID field on an existing OpenSpool tag is not a
   Spoolman numeric ID; linking by card UID avoids rewriting the tag.
6. Reload the spool. The printer resolves the UID, selects that spool's
   material, and assigns its numeric ID to the channel. Use regular Snorca's
   filament sync to import the loaded metadata. Profile matching is the
   slicer's responsibility; this connection does not synchronize calibrated
   slicer profiles.
7. While printing, Moonraker reports used filament length for the active
   spool. SpoolHero converts it using that material's density and diameter
   and subtracts it from that spool's remaining grams. Ownership stays on the
   SpoolHero spool. No tag write-back is performed.

Use a trusted LAN or HTTPS. Anyone with the private URL can read this
project's spool metadata, reassign tag UIDs, and report consumption. Do not
publish the URL in screenshots, proxy access logs, or public issue reports.
SpoolHero stores only its SHA-256 hash and does not include it in exports.
Disable the connection or generate a replacement URL to revoke access.
Existing WebSockets are closed within the next connection check (normally
within two seconds) after revocation.

## Inventory and measurement

- **Initial filament weight** is the spool's original net filament weight.
  It is independent of the shared material and does not change if another
  color or spool's material nominal weight is edited.
- **Remaining filament** is always net grams. Empty spool weight is never
  subtracted from an already net value reported by the API.
- **Empty spool weight** is an optional tare weight. It is exposed as
  `spool_weight`; it is not consumed by printing.
- Consumption is estimated extrusion, not a scale measurement. Retractions,
  purges, material density, calibration, and manual spool swaps can affect
  accuracy. Weighing a spool and correcting net grams remains useful.
- Deductions are atomic and never reduce remaining grams below zero.
  Fractions below 0.01 g accumulate across reports instead of disappearing
  through repeated rounding. Each accepted report is recorded in the audit
  log in the same transaction as its weight update.
- Moonraker's report has no unique request ID. As with native Spoolman, a
  network failure after the server committed but before the reply arrived
  can make a client retry deduct twice. The adapter cannot safely deduplicate
  equal amounts, because repeated equal amounts can represent real usage.
  Normal disconnect buffering and reconnect behavior are handled by Moonraker.

## Transfers and backups

**Export all** includes the per-project numeric spool IDs, all linked card
UIDs, fractional usage, initial and tare weights. Importing into a fresh
installation preserves these associations. Enable a new connection after
restoring and update the URL in the printer; connection credentials are
intentionally excluded from the backup. Normal inventory backup collision
checks apply. Old v1 backups without integration fields remain supported.

## Compatibility surface and limits

The private connection base is `/api/spoolman/<secret>`. Both paxx12's
SpoolLink and Moonraker append `/api/v1` to it. Supported operations:

| Route after `/api/v1` | Behavior |
| --- | --- |
| `GET /info` | Compatibility server information |
| `GET /field/spool`, `/field/filament` | Immutable `card_uids` and `variant` definitions |
| `POST /field/{entity}/{key}` | Ensures either supported definition |
| `GET /spool` | Project-scoped inventory; `limit`, `offset`, `allow_archived` |
| `GET /spool/{id}` | One existing spool with material and vendor metadata |
| `PATCH /spool/{id}` | Links or removes JSON-encoded `extra.card_uids` |
| `PUT /spool/{id}/use` | Exactly one positive `use_length` in mm or `use_weight` in g |
| `WebSocket /spool` | Live connection, keepalive ping/pong, updated/deleted spool events |

Other routes return a JSON 404. This is deliberately a printer integration,
not a replacement for the whole Spoolman API. Third-party apps that create
vendors, materials, spools, arbitrary custom fields, or edit remaining
weights through general Spoolman PATCH routes are not supported. Manage
inventory through SpoolHero.

Numeric IDs are scoped to this project's connection, remain stable, and are
never reused after a spool is deleted. Spoolman-style filament/vendor IDs
are read-only projections scoped to the spool rather than independent
inventory entities. Variants are currently returned empty because
SpoolHero's material model has no dedicated subtype field.

paxx12 v1.6 scans at most 1,000 active spools when resolving a card UID. Its
client does not paginate that query. Libraries larger than that need a
firmware improvement or explicit numeric spool selection. Archived
(consumed) spools remain readable by ID and can be listed with
`allow_archived=true`; they are excluded from the normal UID lookup.

This implementation is checked against the upstream wire contracts and
simulated Moonraker/SpoolLink traffic. Actual printer operation must still
be verified when the U1 is installed.

Upstream references:

- [paxx12 Spoolman setup](https://github.com/paxx12-snapmaker-u1/SnapmakerU1-Extended-Firmware/blob/v1.6.0-paxx12-22/docs/spoolman.md)
- [paxx12 custom fields and flow](https://github.com/paxx12-snapmaker-u1/SnapmakerU1-Extended-Firmware/blob/v1.6.0-paxx12-22/docs/design/spoolman.md)
- [Moonraker Spoolman component](https://github.com/Arksine/moonraker/blob/master/moonraker/components/spoolman.py)
