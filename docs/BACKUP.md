# Backup and transfer

Use **Export all** at the top of the Spools page, or open **Backup & transfer** from your account menu. The same page is available from the mobile menu and before selecting a project.

## Save your inventory

Click **Export all** on the backup page to download one JSON file. It contains all projects where you have the Admin role, including their spools, owners, materials, printers, storage locations, dryers, photos and your material favorites. Spool notes, remaining amounts, dates, IDs and tag associations travel with the inventory.

Login accounts, passwords, project membership and server settings such as email configuration are excluded. Invite your other printer users again after transferring.

## Move to Docker

1. Export your current inventory and keep the downloaded JSON file.
2. Start your Docker installation and sign in with its server administrator account.
3. Open **Backup & transfer**. You can do this before creating a project.
4. Choose the JSON file. Check the displayed project and inventory counts.
5. Optionally select **Also restore the OpenFilamentDB preference**.
6. Click **Import backup**, then **Open imported inventory**.

Import creates the saved projects and keeps the original inventory IDs and relationships. Your imported favorites belong to the account performing the import. Existing records with the same IDs prevent the import; the file is applied as a single operation, so a rejected import leaves the inventory unchanged. A fresh installation is the easiest destination.

Backup files can be up to 64 MB. This inventory export is separate from backing up your Docker database volume and server configuration.
