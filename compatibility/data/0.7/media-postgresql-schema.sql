CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE artwork_references (
    id uuid NOT NULL,
    target_kind text NOT NULL,
    target_id uuid NOT NULL,
    role_code text NOT NULL,
    locator_scheme text NOT NULL,
    locator_value text NOT NULL,
    provenance_origin_code text NOT NULL,
    provenance_provider_code text,
    provenance_identifier_provider_code text,
    provenance_identifier_namespace_code text,
    provenance_identifier_value text,
    provenance_observed_utc_ticks bigint NOT NULL,
    provenance_observed_offset_minutes smallint NOT NULL,
    provenance_confidence text,
    CONSTRAINT "PK_artwork_references" PRIMARY KEY (id)
);

CREATE TABLE assets (
    id uuid NOT NULL,
    media_kind_code text NOT NULL,
    CONSTRAINT "PK_assets" PRIMARY KEY (id)
);

CREATE TABLE external_identifier_association_relations (
    association_id uuid NOT NULL,
    related_association_id uuid NOT NULL,
    kind_code text NOT NULL,
    reason text,
    evidence_origin_code text NOT NULL,
    evidence_provider_code text,
    evidence_identifier_provider_code text,
    evidence_identifier_namespace_code text,
    evidence_identifier_value text,
    evidence_observed_utc_ticks bigint NOT NULL,
    evidence_observed_offset_minutes smallint NOT NULL,
    evidence_confidence text,
    CONSTRAINT "PK_external_identifier_association_relations" PRIMARY KEY (association_id, related_association_id, kind_code)
);

CREATE TABLE external_identifier_associations (
    id uuid NOT NULL,
    provider_code text NOT NULL,
    namespace_code text NOT NULL,
    identifier_value text NOT NULL,
    current_status_code text NOT NULL,
    target_kind text NOT NULL,
    target_id uuid NOT NULL,
    CONSTRAINT "PK_external_identifier_associations" PRIMARY KEY (id)
);

CREATE TABLE external_identifier_lifecycle_entries (
    id uuid NOT NULL,
    association_id uuid NOT NULL,
    ordinal integer NOT NULL,
    status_code text NOT NULL,
    occurred_utc_ticks bigint NOT NULL,
    occurred_offset_minutes smallint NOT NULL,
    reason text,
    evidence_origin_code text NOT NULL,
    evidence_provider_code text,
    evidence_identifier_provider_code text,
    evidence_identifier_namespace_code text,
    evidence_identifier_value text,
    evidence_observed_utc_ticks bigint NOT NULL,
    evidence_observed_offset_minutes smallint NOT NULL,
    evidence_confidence text,
    CONSTRAINT "PK_external_identifier_lifecycle_entries" PRIMARY KEY (id)
);

CREATE TABLE library_imports (
    id uuid NOT NULL,
    root_key text NOT NULL,
    relative_path text NOT NULL,
    status_code text NOT NULL,
    observed_length bigint,
    observed_utc_ticks bigint,
    observed_offset_minutes smallint,
    successful_length bigint,
    successful_utc_ticks bigint,
    successful_offset_minutes smallint,
    work_id uuid,
    media_entity_id uuid,
    timeline_id uuid,
    source_id uuid,
    video_asset_id uuid,
    video_representation_id uuid,
    video_coverage_id uuid,
    audio_asset_id uuid,
    audio_representation_id uuid,
    audio_coverage_id uuid,
    CONSTRAINT "PK_library_imports" PRIMARY KEY (id)
);

CREATE TABLE media_sources (
    id uuid NOT NULL,
    scheme text NOT NULL,
    opaque_value text NOT NULL,
    CONSTRAINT "PK_media_sources" PRIMARY KEY (id)
);

CREATE TABLE metadata_assertions (
    id uuid NOT NULL,
    target_kind text NOT NULL,
    target_id uuid NOT NULL,
    field_code text NOT NULL,
    value_kind text NOT NULL,
    text_value text,
    integer_value bigint,
    decimal_value text,
    boolean_value boolean,
    date_value text,
    utc_ticks bigint,
    offset_minutes smallint,
    status_code text NOT NULL,
    superseded_by_assertion_id uuid,
    provenance_origin text NOT NULL,
    provenance_provider_code text,
    provenance_identifier_provider_code text,
    provenance_identifier_namespace_code text,
    provenance_identifier_value text,
    provenance_observed_utc_ticks bigint NOT NULL,
    provenance_observed_offset_minutes smallint NOT NULL,
    provenance_confidence text,
    CONSTRAINT "PK_metadata_assertions" PRIMARY KEY (id)
);

CREATE TABLE metadata_credits (
    id uuid NOT NULL,
    target_id uuid NOT NULL,
    person_id uuid NOT NULL,
    credit_type text NOT NULL,
    character text,
    job text,
    ordinal integer,
    provider_code text NOT NULL,
    observed_utc_ticks bigint NOT NULL,
    CONSTRAINT "PK_metadata_credits" PRIMARY KEY (id)
);

CREATE TABLE metadata_field_locks (
    work_id uuid NOT NULL,
    field_code text NOT NULL,
    is_locked boolean NOT NULL,
    CONSTRAINT "PK_metadata_field_locks" PRIMARY KEY (work_id, field_code)
);

CREATE TABLE metadata_genres (
    id uuid NOT NULL,
    target_id uuid NOT NULL,
    name text NOT NULL,
    normalized_name text NOT NULL,
    ordinal integer NOT NULL,
    provider_code text NOT NULL,
    observed_utc_ticks bigint NOT NULL,
    CONSTRAINT "PK_metadata_genres" PRIMARY KEY (id)
);

CREATE TABLE metadata_persons (
    id uuid NOT NULL,
    provider_code text NOT NULL,
    external_id text NOT NULL,
    name text NOT NULL,
    profile_artwork_locator text,
    biography text,
    birthday text,
    deathday text,
    place_of_birth text,
    profile_observed_utc_ticks bigint,
    CONSTRAINT "PK_metadata_persons" PRIMARY KEY (id)
);

CREATE TABLE metadata_refresh_states (
    id uuid NOT NULL,
    target_kind text NOT NULL,
    target_id uuid NOT NULL,
    provider_code text NOT NULL,
    external_identifier_key text NOT NULL,
    external_identifier_provider_code text,
    external_identifier_namespace_code text,
    external_identifier_value text,
    last_attempt_utc_ticks bigint,
    last_attempt_offset_minutes smallint,
    last_success_utc_ticks bigint,
    last_success_offset_minutes smallint,
    mapping_version text NOT NULL,
    payload_fingerprint_key text NOT NULL,
    payload_fingerprint text,
    status_code text NOT NULL,
    CONSTRAINT "PK_metadata_refresh_states" PRIMARY KEY (id)
);

CREATE TABLE metadata_settings (
    work_id uuid NOT NULL,
    language text,
    region text,
    lock_item_metadata boolean NOT NULL,
    CONSTRAINT "PK_metadata_settings" PRIMARY KEY (work_id)
);

CREATE TABLE metadata_studios (
    id uuid NOT NULL,
    target_id uuid NOT NULL,
    external_id text NOT NULL,
    name text NOT NULL,
    ordinal integer NOT NULL,
    provider_code text NOT NULL,
    observed_utc_ticks bigint NOT NULL,
    CONSTRAINT "PK_metadata_studios" PRIMARY KEY (id)
);

CREATE TABLE metadata_tags (
    id uuid NOT NULL,
    target_id uuid NOT NULL,
    name text NOT NULL,
    normalized_name text NOT NULL,
    ordinal integer NOT NULL,
    provider_code text NOT NULL,
    observed_utc_ticks bigint NOT NULL,
    CONSTRAINT "PK_metadata_tags" PRIMARY KEY (id)
);

CREATE TABLE works (
    id uuid NOT NULL,
    category_code text NOT NULL,
    CONSTRAINT "PK_works" PRIMARY KEY (id)
);

CREATE TABLE asset_roles (
    asset_id uuid NOT NULL,
    role_code text NOT NULL,
    CONSTRAINT "PK_asset_roles" PRIMARY KEY (asset_id, role_code),
    CONSTRAINT "FK_asset_roles_assets_asset_id" FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE CASCADE
);

CREATE TABLE technical_representations (
    id uuid NOT NULL,
    asset_id uuid NOT NULL,
    profile_kind text NOT NULL,
    codec_namespace text NOT NULL,
    codec_code text NOT NULL,
    width integer,
    height integer,
    frame_rate_numerator integer,
    frame_rate_denominator integer,
    bit_depth integer,
    bit_rate bigint,
    sample_rate integer,
    channel_count integer,
    CONSTRAINT "PK_technical_representations" PRIMARY KEY (id),
    CONSTRAINT "FK_technical_representations_assets_asset_id" FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE CASCADE
);

CREATE TABLE media_entities (
    id uuid NOT NULL,
    work_id uuid NOT NULL,
    category_code text NOT NULL,
    CONSTRAINT "PK_media_entities" PRIMARY KEY (id),
    CONSTRAINT "FK_media_entities_works_work_id" FOREIGN KEY (work_id) REFERENCES works (id) ON DELETE CASCADE
);

CREATE TABLE representation_source_bindings (
    representation_id uuid NOT NULL,
    source_id uuid NOT NULL,
    container_namespace text,
    container_code text,
    stream_index integer,
    CONSTRAINT "PK_representation_source_bindings" PRIMARY KEY (representation_id, source_id),
    CONSTRAINT "FK_representation_source_bindings_media_sources_source_id" FOREIGN KEY (source_id) REFERENCES media_sources (id) ON DELETE CASCADE,
    CONSTRAINT "FK_representation_source_bindings_technical_representations_re~" FOREIGN KEY (representation_id) REFERENCES technical_representations (id) ON DELETE CASCADE
);

CREATE TABLE timelines (
    id uuid NOT NULL,
    media_entity_id uuid NOT NULL,
    duration_ticks bigint,
    CONSTRAINT "PK_timelines" PRIMARY KEY (id),
    CONSTRAINT "FK_timelines_media_entities_media_entity_id" FOREIGN KEY (media_entity_id) REFERENCES media_entities (id) ON DELETE CASCADE
);

CREATE TABLE asset_coverages (
    id uuid NOT NULL,
    timeline_id uuid NOT NULL,
    asset_id uuid NOT NULL,
    state_code text NOT NULL,
    coverage_kind_code text,
    alignment_offset_ticks bigint,
    CONSTRAINT "PK_asset_coverages" PRIMARY KEY (id),
    CONSTRAINT "FK_asset_coverages_assets_asset_id" FOREIGN KEY (asset_id) REFERENCES assets (id) ON DELETE CASCADE,
    CONSTRAINT "FK_asset_coverages_timelines_timeline_id" FOREIGN KEY (timeline_id) REFERENCES timelines (id) ON DELETE CASCADE
);

CREATE INDEX "IX_asset_coverages_asset_id" ON asset_coverages (asset_id);

CREATE UNIQUE INDEX "IX_asset_coverages_timeline_id_asset_id" ON asset_coverages (timeline_id, asset_id);

CREATE UNIQUE INDEX ux_confirmed_external_identifier ON external_identifier_associations (provider_code, namespace_code, identifier_value) WHERE current_status_code = 'confirmed';

CREATE UNIQUE INDEX "IX_external_identifier_lifecycle_entries_association_id_ordinal" ON external_identifier_lifecycle_entries (association_id, ordinal);

CREATE UNIQUE INDEX "IX_library_imports_root_key_relative_path" ON library_imports (root_key, relative_path);

CREATE INDEX "IX_media_entities_work_id" ON media_entities (work_id);

CREATE UNIQUE INDEX "IX_metadata_credits_target_id_person_id_credit_type_provider_c~" ON metadata_credits (target_id, person_id, credit_type, provider_code);

CREATE UNIQUE INDEX "IX_metadata_genres_target_id_normalized_name_provider_code" ON metadata_genres (target_id, normalized_name, provider_code);

CREATE UNIQUE INDEX "IX_metadata_persons_provider_code_external_id" ON metadata_persons (provider_code, external_id);

CREATE UNIQUE INDEX "IX_metadata_refresh_states_target_kind_target_id_provider_code~" ON metadata_refresh_states (target_kind, target_id, provider_code, external_identifier_key, mapping_version, payload_fingerprint_key);

CREATE UNIQUE INDEX "IX_metadata_studios_target_id_external_id_provider_code" ON metadata_studios (target_id, external_id, provider_code);

CREATE UNIQUE INDEX "IX_metadata_tags_target_id_normalized_name_provider_code" ON metadata_tags (target_id, normalized_name, provider_code);

CREATE INDEX "IX_representation_source_bindings_source_id" ON representation_source_bindings (source_id);

CREATE INDEX "IX_technical_representations_asset_id" ON technical_representations (asset_id);

CREATE INDEX "IX_timelines_media_entity_id" ON timelines (media_entity_id);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260926104649_InitialPostgreSql', '10.0.12');

COMMIT;

