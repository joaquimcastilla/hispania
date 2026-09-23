using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hispania.Library.Persistence.Sqlite.Migrations;

[DbContext(typeof(LibraryCatalogDbContext))]
[Migration("20260922000300_rich_metadata")]
public sealed class RichMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
CREATE TABLE metadata_genres (id BLOB PRIMARY KEY, target_id BLOB NOT NULL, name TEXT NOT NULL, normalized_name TEXT NOT NULL, ordinal INTEGER NOT NULL, provider_code TEXT NOT NULL, observed_utc_ticks INTEGER NOT NULL);
CREATE UNIQUE INDEX ux_metadata_genres ON metadata_genres(target_id, normalized_name, provider_code);
CREATE TABLE metadata_tags (id BLOB PRIMARY KEY, target_id BLOB NOT NULL, name TEXT NOT NULL, normalized_name TEXT NOT NULL, ordinal INTEGER NOT NULL, provider_code TEXT NOT NULL, observed_utc_ticks INTEGER NOT NULL);
CREATE UNIQUE INDEX ux_metadata_tags ON metadata_tags(target_id, normalized_name, provider_code);
CREATE TABLE metadata_studios (id BLOB PRIMARY KEY, target_id BLOB NOT NULL, external_id TEXT NOT NULL, name TEXT NOT NULL, ordinal INTEGER NOT NULL, provider_code TEXT NOT NULL, observed_utc_ticks INTEGER NOT NULL);
CREATE UNIQUE INDEX ux_metadata_studios ON metadata_studios(target_id, external_id, provider_code);
CREATE TABLE metadata_persons (id BLOB PRIMARY KEY, provider_code TEXT NOT NULL, external_id TEXT NOT NULL, name TEXT NOT NULL, profile_artwork_locator TEXT NULL);
CREATE UNIQUE INDEX ux_metadata_persons ON metadata_persons(provider_code, external_id);
CREATE TABLE metadata_credits (id BLOB PRIMARY KEY, target_id BLOB NOT NULL, person_id BLOB NOT NULL, credit_type TEXT NOT NULL, character TEXT NULL, job TEXT NULL, ordinal INTEGER NULL, provider_code TEXT NOT NULL, observed_utc_ticks INTEGER NOT NULL);
CREATE UNIQUE INDEX ux_metadata_credits ON metadata_credits(target_id, person_id, credit_type, provider_code);
""");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS metadata_credits; DROP TABLE IF EXISTS metadata_persons; DROP TABLE IF EXISTS metadata_studios; DROP TABLE IF EXISTS metadata_tags; DROP TABLE IF EXISTS metadata_genres;");
}
