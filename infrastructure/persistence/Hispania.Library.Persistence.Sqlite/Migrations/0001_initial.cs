using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hispania.Library.Persistence.Sqlite.Migrations;

[DbContext(typeof(LibraryCatalogDbContext))]
[Migration("0001_initial")]
public sealed class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE library_imports (id BLOB PRIMARY KEY, root_key TEXT NOT NULL, relative_path TEXT NOT NULL, status_code TEXT NOT NULL CHECK(status_code IN ('active','disappeared','unsupported','probe-failed')), observed_length INTEGER NULL, observed_utc_ticks INTEGER NULL, observed_offset_minutes INTEGER NULL, successful_length INTEGER NULL, successful_utc_ticks INTEGER NULL, successful_offset_minutes INTEGER NULL, work_id BLOB NULL, media_entity_id BLOB NULL, timeline_id BLOB NULL, source_id BLOB NULL, video_asset_id BLOB NULL, video_representation_id BLOB NULL, video_coverage_id BLOB NULL, audio_asset_id BLOB NULL, audio_representation_id BLOB NULL, audio_coverage_id BLOB NULL, UNIQUE(root_key, relative_path));
CREATE TABLE works (id BLOB PRIMARY KEY, category_code TEXT NOT NULL);
CREATE TABLE media_entities (id BLOB PRIMARY KEY, work_id BLOB NOT NULL, category_code TEXT NOT NULL, FOREIGN KEY(work_id) REFERENCES works(id));
CREATE TABLE timelines (id BLOB PRIMARY KEY, media_entity_id BLOB NOT NULL, duration_ticks INTEGER NULL, FOREIGN KEY(media_entity_id) REFERENCES media_entities(id));
CREATE TABLE assets (id BLOB PRIMARY KEY, media_kind_code TEXT NOT NULL);
CREATE TABLE asset_roles (asset_id BLOB NOT NULL, role_code TEXT NOT NULL, PRIMARY KEY(asset_id,role_code), FOREIGN KEY(asset_id) REFERENCES assets(id));
CREATE TABLE technical_representations (id BLOB PRIMARY KEY, asset_id BLOB NOT NULL, profile_kind TEXT NOT NULL, codec_namespace TEXT NOT NULL, codec_code TEXT NOT NULL, width INTEGER NULL, height INTEGER NULL, frame_rate_numerator INTEGER NULL, frame_rate_denominator INTEGER NULL, bit_depth INTEGER NULL, bit_rate INTEGER NULL, sample_rate INTEGER NULL, channel_count INTEGER NULL, FOREIGN KEY(asset_id) REFERENCES assets(id));
CREATE TABLE media_sources (id BLOB PRIMARY KEY, scheme TEXT NOT NULL, opaque_value TEXT NOT NULL);
CREATE TABLE representation_source_bindings (representation_id BLOB NOT NULL, source_id BLOB NOT NULL, container_namespace TEXT NULL, container_code TEXT NULL, stream_index INTEGER NULL, PRIMARY KEY(representation_id,source_id), FOREIGN KEY(representation_id) REFERENCES technical_representations(id), FOREIGN KEY(source_id) REFERENCES media_sources(id));
CREATE TABLE asset_coverages (id BLOB PRIMARY KEY, timeline_id BLOB NOT NULL, asset_id BLOB NOT NULL, state_code TEXT NOT NULL, coverage_kind_code TEXT NULL, alignment_offset_ticks INTEGER NULL, UNIQUE(timeline_id,asset_id), FOREIGN KEY(timeline_id) REFERENCES timelines(id), FOREIGN KEY(asset_id) REFERENCES assets(id));
CREATE TABLE asset_coverage_intervals (coverage_id BLOB NOT NULL, ordinal INTEGER NOT NULL, start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL, PRIMARY KEY(coverage_id,ordinal), FOREIGN KEY(coverage_id) REFERENCES asset_coverages(id));
CREATE TABLE video_technical_profiles (representation_id BLOB PRIMARY KEY, width INTEGER NOT NULL, height INTEGER NOT NULL, FOREIGN KEY(representation_id) REFERENCES technical_representations(id));
CREATE TABLE audio_technical_profiles (representation_id BLOB PRIMARY KEY, sample_rate INTEGER NOT NULL, channel_count INTEGER NOT NULL, FOREIGN KEY(representation_id) REFERENCES technical_representations(id));
CREATE TABLE text_technical_profiles (representation_id BLOB PRIMARY KEY, format_code TEXT NOT NULL);
CREATE TABLE image_technical_profiles (representation_id BLOB PRIMARY KEY, format_code TEXT NOT NULL);
CREATE TABLE releases (id BLOB PRIMARY KEY, category_code TEXT NOT NULL);
CREATE TABLE release_media_entity_memberships (release_id BLOB NOT NULL, media_entity_id BLOB NOT NULL, PRIMARY KEY(release_id,media_entity_id));
CREATE TABLE release_groups (id BLOB PRIMARY KEY, category_code TEXT NOT NULL);
CREATE TABLE release_group_memberships (release_group_id BLOB NOT NULL, release_id BLOB NOT NULL, PRIMARY KEY(release_group_id,release_id));
CREATE TABLE collections (id BLOB PRIMARY KEY, category_code TEXT NOT NULL);
CREATE TABLE collection_memberships (id BLOB PRIMARY KEY, collection_id BLOB NOT NULL, media_entity_id BLOB NOT NULL, position_code TEXT NOT NULL, position_value TEXT NOT NULL, UNIQUE(collection_id,position_code,position_value));
CREATE TABLE creators (id BLOB PRIMARY KEY, kind_code TEXT NOT NULL);
CREATE TABLE creator_contributions (id BLOB PRIMARY KEY, creator_id BLOB NOT NULL, target_kind TEXT NOT NULL, target_id BLOB NOT NULL, role_code TEXT NOT NULL, UNIQUE(creator_id,target_kind,target_id,role_code));
CREATE TABLE external_identifier_associations (id BLOB PRIMARY KEY, provider_code TEXT NOT NULL, namespace_code TEXT NOT NULL, identifier_value TEXT NOT NULL, current_status_code TEXT NOT NULL, target_kind TEXT NOT NULL, target_id BLOB NOT NULL);
CREATE UNIQUE INDEX ux_confirmed_external_identifier ON external_identifier_associations(provider_code,namespace_code,identifier_value) WHERE current_status_code='confirmed';
CREATE TABLE external_identifier_lifecycle_entries (id BLOB PRIMARY KEY, association_id BLOB NOT NULL, status_code TEXT NOT NULL, occurred_utc_ticks INTEGER NOT NULL, occurred_offset_minutes INTEGER NOT NULL);
CREATE TABLE external_identifier_association_relations (association_id BLOB NOT NULL, related_association_id BLOB NOT NULL, kind_code TEXT NOT NULL, PRIMARY KEY(association_id,related_association_id,kind_code));
CREATE TABLE metadata_assertions (id BLOB PRIMARY KEY, target_kind TEXT NOT NULL, target_id BLOB NOT NULL, field_code TEXT NOT NULL, value_kind TEXT NOT NULL, text_value TEXT NULL, decimal_value TEXT NULL, utc_ticks INTEGER NULL, offset_minutes INTEGER NULL, status_code TEXT NOT NULL, provenance_origin TEXT NOT NULL);
CREATE TABLE artwork_references (id BLOB PRIMARY KEY, target_kind TEXT NOT NULL, target_id BLOB NOT NULL, role_code TEXT NOT NULL, locator_scheme TEXT NOT NULL, locator_value TEXT NOT NULL);
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS library_imports;");
}
