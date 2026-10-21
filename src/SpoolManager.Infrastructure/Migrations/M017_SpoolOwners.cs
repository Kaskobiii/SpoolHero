using FluentMigrator;

namespace SpoolManager.Infrastructure.Migrations;

[Migration(17)]
public class M017_SpoolOwners : Migration
{
    public override void Up()
    {
        Create.Table("spool_owners")
            .WithColumn("id").AsString(36).PrimaryKey()
            .WithColumn("project_id").AsString(36).NotNullable()
            .WithColumn("name").AsString(100).NotNullable();

        // Owner names support Unicode, including emoji.
        Execute.Sql("ALTER TABLE spool_owners CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
        // Older installations may retain a different collation on projects.id.
        Execute.WithConnection((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT CHARACTER_SET_NAME, COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'projects' AND COLUMN_NAME = 'id'";
            string charset, collation;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("projects.id is missing.");
                charset = reader.GetString(0);
                collation = reader.GetString(1);
            }
            command.CommandText = $"ALTER TABLE spool_owners MODIFY project_id VARCHAR(36) CHARACTER SET `{charset.Replace("`", "``")}` COLLATE `{collation.Replace("`", "``")}` NOT NULL";
            command.ExecuteNonQuery();
        });
        Create.ForeignKey("fk_owners_project").FromTable("spool_owners").ForeignColumn("project_id")
            .ToTable("projects").PrimaryColumn("id").OnDelete(System.Data.Rule.Cascade);
        Create.Index("ux_owners_project_name").OnTable("spool_owners")
            .OnColumn("project_id").Ascending().OnColumn("name").Ascending().WithOptions().Unique();

        Alter.Table("spools").AddColumn("owner_id").AsString(36).Nullable();
        Execute.Sql("ALTER TABLE spools MODIFY owner_id VARCHAR(36) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL");
        Create.ForeignKey("fk_spools_owner").FromTable("spools").ForeignColumn("owner_id")
            .ToTable("spool_owners").PrimaryColumn("id").OnDelete(System.Data.Rule.SetNull);
    }

    public override void Down()
    {
        Delete.ForeignKey("fk_spools_owner").OnTable("spools");
        Delete.Column("owner_id").FromTable("spools");
        Delete.Table("spool_owners");
    }
}
