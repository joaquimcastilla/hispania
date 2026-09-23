using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Hispania.Library.Persistence.Sqlite.Migrations;
[DbContext(typeof(LibraryCatalogDbContext))]
[Migration("20260922000400_metadata_settings")]
public sealed class MetadataSettings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("CREATE TABLE metadata_settings (work_id BLOB PRIMARY KEY, language TEXT NULL, region TEXT NULL, lock_item_metadata INTEGER NOT NULL DEFAULT 0); CREATE TABLE metadata_field_locks (work_id BLOB NOT NULL, field_code TEXT NOT NULL, is_locked INTEGER NOT NULL, PRIMARY KEY(work_id, field_code));");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS metadata_field_locks; DROP TABLE IF EXISTS metadata_settings;");
}
