using FluentMigrator;

namespace SpoolManager.Infrastructure.Migrations;

[Migration(18, TransactionBehavior.None)]
public class M018_InventoryTextUnicode : ForwardOnlyMigration
{
    public override void Up()
    {
        // M014's whole-table conversion can be blocked by GUID foreign keys on
        // existing MySQL/MariaDB installations. Convert only inventory text so
        // Unicode names and notes survive backup/restore without changing IDs,
        // foreign-key collations, column sizes or nullability.
        Execute.Sql("""
            ALTER TABLE `projects`
                MODIFY COLUMN `name` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `description` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
        Execute.Sql("""
            ALTER TABLE `filament_materials`
                MODIFY COLUMN `type` VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `color_hex` VARCHAR(7) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `brand` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `color_name` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `notes` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `reorder_url` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `ofd_filament_id` VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `ofd_variant_id` VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
        Execute.Sql("""
            ALTER TABLE `printers`
                MODIFY COLUMN `name` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `notes` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `rfid_tag_uid` VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `image_content_type` VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
        Execute.Sql("""
            ALTER TABLE `storage_locations`
                MODIFY COLUMN `name` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `description` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `rfid_tag_uid` VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `image_content_type` VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
        Execute.Sql("""
            ALTER TABLE `dryers`
                MODIFY COLUMN `name` VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
                MODIFY COLUMN `description` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `rfid_tag_uid` VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `image_content_type` VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
        Execute.Sql("""
            ALTER TABLE `spools`
                MODIFY COLUMN `rfid_tag_uid` VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `notes` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
                MODIFY COLUMN `reorder_url` VARCHAR(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL
            """);
    }
}
