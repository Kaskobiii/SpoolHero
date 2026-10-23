using FluentMigrator;

namespace SpoolManager.Infrastructure.Migrations;

[Migration(19)]
public class M019_SpoolmanCompatibility : Migration
{
    public override void Up()
    {
        Alter.Table("spools").AddColumn("initial_weight_grams").AsDecimal(8, 2).Nullable();
        Execute.Sql("UPDATE spools s JOIN filament_materials m ON m.id=s.filament_material_id SET s.initial_weight_grams=CASE WHEN m.weight_grams>0 THEN m.weight_grams ELSE GREATEST(0,s.remaining_weight_grams) END");
        Execute.Sql("UPDATE spools SET remaining_percent=CASE WHEN initial_weight_grams>0 THEN LEAST(100,GREATEST(0,remaining_weight_grams/initial_weight_grams*100)) ELSE 0 END");
        Execute.Sql("ALTER TABLE spools MODIFY updated_at DATETIME(6) NOT NULL");
        Alter.Table("spools").AddColumn("empty_spool_weight_grams").AsDecimal(8, 2).Nullable();
        Create.Table("spoolman_integrations")
            .WithColumn("project_id").AsString(36).PrimaryKey()
            .WithColumn("token_hash").AsString(64).Nullable()
            .WithColumn("token_created_at").AsDateTime().Nullable()
            .WithColumn("last_seen_at").AsDateTime().Nullable()
            .WithColumn("last_usage_at").AsDateTime().Nullable()
            .WithColumn("next_spool_id").AsInt32().NotNullable().WithDefaultValue(1);
        Create.Index("ux_spoolman_token").OnTable("spoolman_integrations").OnColumn("token_hash").Unique();
        Create.Table("spoolman_spools")
            .WithColumn("project_id").AsString(36).NotNullable()
            .WithColumn("id").AsInt32().NotNullable()
            .WithColumn("spool_id").AsString(36).NotNullable()
            .WithColumn("card_uids").AsString(2048).NotNullable().WithDefaultValue("")
            .WithColumn("usage_remainder_grams").AsDecimal(14, 10).NotNullable().WithDefaultValue(0);
        Create.PrimaryKey("pk_spoolman_spools").OnTable("spoolman_spools").Columns("project_id", "id");
        Create.Index("ux_spoolman_spool").OnTable("spoolman_spools").OnColumn("project_id").Ascending().OnColumn("spool_id").Ascending().WithOptions().Unique();
        // Match legacy GUID collations before adding foreign keys.
        Execute.WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            foreach (var (table, column, parent) in new[] {
                ("spoolman_integrations", "project_id", "projects"),
                ("spoolman_spools", "project_id", "projects"),
                ("spoolman_spools", "spool_id", "spools") })
            {
                command.CommandText = $"SELECT CHARACTER_SET_NAME, COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='{parent}' AND COLUMN_NAME='id'";
                string charset, collation;
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidOperationException($"{parent}.id is missing.");
                    charset = reader.GetString(0); collation = reader.GetString(1);
                }
                command.CommandText = $"ALTER TABLE `{table}` MODIFY `{column}` VARCHAR(36) CHARACTER SET `{charset.Replace("`", "``")}` COLLATE `{collation.Replace("`", "``")}` NOT NULL";
                command.ExecuteNonQuery();
            }
        });
        Create.ForeignKey("fk_spoolman_integration_project").FromTable("spoolman_integrations").ForeignColumn("project_id").ToTable("projects").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_spoolman_map_project").FromTable("spoolman_spools").ForeignColumn("project_id").ToTable("projects").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.ForeignKey("fk_spoolman_map_spool").FromTable("spoolman_spools").ForeignColumn("spool_id").ToTable("spools").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
    }
    public override void Down()
    {
        Delete.Table("spoolman_spools"); Delete.Table("spoolman_integrations");
        Delete.Column("empty_spool_weight_grams").FromTable("spools");
        Delete.Column("initial_weight_grams").FromTable("spools");
    }
}
