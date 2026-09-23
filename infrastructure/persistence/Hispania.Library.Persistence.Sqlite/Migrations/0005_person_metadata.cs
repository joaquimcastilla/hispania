using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hispania.Library.Persistence.Sqlite.Migrations;

[DbContext(typeof(LibraryCatalogDbContext))]
[Migration("20260923000500_person_metadata")]
public sealed class PersonMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE metadata_persons ADD COLUMN biography TEXT NULL; ALTER TABLE metadata_persons ADD COLUMN birthday TEXT NULL; ALTER TABLE metadata_persons ADD COLUMN deathday TEXT NULL; ALTER TABLE metadata_persons ADD COLUMN place_of_birth TEXT NULL; ALTER TABLE metadata_persons ADD COLUMN profile_observed_utc_ticks INTEGER NULL;");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE metadata_persons DROP COLUMN biography; ALTER TABLE metadata_persons DROP COLUMN birthday; ALTER TABLE metadata_persons DROP COLUMN deathday; ALTER TABLE metadata_persons DROP COLUMN place_of_birth; ALTER TABLE metadata_persons DROP COLUMN profile_observed_utc_ticks;");
}
