using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hispania.Library.Persistence.Sqlite.Migrations;

[DbContext(typeof(LibraryCatalogDbContext))]
[Migration("0002_metadata_enrichment")]
public sealed class MetadataEnrichment : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN ordinal INTEGER NOT NULL DEFAULT 0;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN reason TEXT NULL;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_origin_code TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_provider_code TEXT NULL;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_identifier_provider_code TEXT NULL;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_identifier_namespace_code TEXT NULL;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_identifier_value TEXT NULL;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_observed_utc_ticks INTEGER NOT NULL DEFAULT 0;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_observed_offset_minutes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE external_identifier_lifecycle_entries ADD COLUMN evidence_confidence TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN reason TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_origin_code TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_provider_code TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_identifier_provider_code TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_identifier_namespace_code TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_identifier_value TEXT NULL;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_observed_utc_ticks INTEGER NOT NULL DEFAULT 0;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_observed_offset_minutes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE external_identifier_association_relations ADD COLUMN evidence_confidence TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN integer_value INTEGER NULL;
ALTER TABLE metadata_assertions ADD COLUMN boolean_value INTEGER NULL;
ALTER TABLE metadata_assertions ADD COLUMN date_value TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN superseded_by_assertion_id BLOB NULL;
ALTER TABLE metadata_assertions ADD COLUMN provenance_origin_code TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE metadata_assertions ADD COLUMN provenance_provider_code TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN provenance_identifier_provider_code TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN provenance_identifier_namespace_code TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN provenance_identifier_value TEXT NULL;
ALTER TABLE metadata_assertions ADD COLUMN provenance_observed_utc_ticks INTEGER NOT NULL DEFAULT 0;
ALTER TABLE metadata_assertions ADD COLUMN provenance_observed_offset_minutes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE metadata_assertions ADD COLUMN provenance_confidence TEXT NULL;
ALTER TABLE artwork_references ADD COLUMN provenance_origin_code TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE artwork_references ADD COLUMN provenance_provider_code TEXT NULL;
ALTER TABLE artwork_references ADD COLUMN provenance_identifier_provider_code TEXT NULL;
ALTER TABLE artwork_references ADD COLUMN provenance_identifier_namespace_code TEXT NULL;
ALTER TABLE artwork_references ADD COLUMN provenance_identifier_value TEXT NULL;
ALTER TABLE artwork_references ADD COLUMN provenance_observed_utc_ticks INTEGER NOT NULL DEFAULT 0;
ALTER TABLE artwork_references ADD COLUMN provenance_observed_offset_minutes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE artwork_references ADD COLUMN provenance_confidence TEXT NULL;
CREATE UNIQUE INDEX ux_external_identifier_lifecycle_ordinal ON external_identifier_lifecycle_entries(association_id, ordinal);
CREATE TABLE metadata_refresh_states (id BLOB PRIMARY KEY, target_kind TEXT NOT NULL, target_id BLOB NOT NULL, provider_code TEXT NOT NULL, external_identifier_key TEXT NOT NULL, external_identifier_provider_code TEXT NULL, external_identifier_namespace_code TEXT NULL, external_identifier_value TEXT NULL, last_attempt_utc_ticks INTEGER NULL, last_attempt_offset_minutes INTEGER NULL, last_success_utc_ticks INTEGER NULL, last_success_offset_minutes INTEGER NULL, mapping_version TEXT NOT NULL, payload_fingerprint_key TEXT NOT NULL, payload_fingerprint TEXT NULL, status_code TEXT NOT NULL CHECK(status_code IN ('succeeded','failed')));
CREATE UNIQUE INDEX ux_metadata_refresh_idempotency ON metadata_refresh_states(target_kind, target_id, provider_code, external_identifier_key, mapping_version, payload_fingerprint_key);
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS metadata_refresh_states;");
}
