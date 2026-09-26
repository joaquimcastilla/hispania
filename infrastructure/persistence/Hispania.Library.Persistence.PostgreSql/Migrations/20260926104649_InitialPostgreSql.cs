using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hispania.Library.Persistence.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artwork_references",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "text", nullable: false),
                    locator_scheme = table.Column<string>(type: "text", nullable: false),
                    locator_value = table.Column<string>(type: "text", nullable: false),
                    provenance_origin_code = table.Column<string>(type: "text", nullable: false),
                    provenance_provider_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_provider_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_namespace_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_value = table.Column<string>(type: "text", nullable: true),
                    provenance_observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    provenance_observed_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    provenance_confidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artwork_references", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_kind_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_identifier_association_relations",
                columns: table => new
                {
                    association_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_association_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind_code = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    evidence_origin_code = table.Column<string>(type: "text", nullable: false),
                    evidence_provider_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_provider_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_namespace_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_value = table.Column<string>(type: "text", nullable: true),
                    evidence_observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    evidence_observed_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    evidence_confidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identifier_association_relations", x => new { x.association_id, x.related_association_id, x.kind_code });
                });

            migrationBuilder.CreateTable(
                name: "external_identifier_associations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    namespace_code = table.Column<string>(type: "text", nullable: false),
                    identifier_value = table.Column<string>(type: "text", nullable: false),
                    current_status_code = table.Column<string>(type: "text", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identifier_associations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_identifier_lifecycle_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    association_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<string>(type: "text", nullable: false),
                    occurred_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    occurred_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    evidence_origin_code = table.Column<string>(type: "text", nullable: false),
                    evidence_provider_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_provider_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_namespace_code = table.Column<string>(type: "text", nullable: true),
                    evidence_identifier_value = table.Column<string>(type: "text", nullable: true),
                    evidence_observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    evidence_observed_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    evidence_confidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_identifier_lifecycle_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "library_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    root_key = table.Column<string>(type: "text", nullable: false),
                    relative_path = table.Column<string>(type: "text", nullable: false),
                    status_code = table.Column<string>(type: "text", nullable: false),
                    observed_length = table.Column<long>(type: "bigint", nullable: true),
                    observed_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    observed_offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    successful_length = table.Column<long>(type: "bigint", nullable: true),
                    successful_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    successful_offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    work_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    video_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    video_representation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    video_coverage_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audio_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audio_representation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audio_coverage_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_imports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheme = table.Column<string>(type: "text", nullable: false),
                    opaque_value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_sources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_assertions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_code = table.Column<string>(type: "text", nullable: false),
                    value_kind = table.Column<string>(type: "text", nullable: false),
                    text_value = table.Column<string>(type: "text", nullable: true),
                    integer_value = table.Column<long>(type: "bigint", nullable: true),
                    decimal_value = table.Column<string>(type: "text", nullable: true),
                    boolean_value = table.Column<bool>(type: "boolean", nullable: true),
                    date_value = table.Column<string>(type: "text", nullable: true),
                    utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    status_code = table.Column<string>(type: "text", nullable: false),
                    superseded_by_assertion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provenance_origin = table.Column<string>(type: "text", nullable: false),
                    provenance_provider_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_provider_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_namespace_code = table.Column<string>(type: "text", nullable: true),
                    provenance_identifier_value = table.Column<string>(type: "text", nullable: true),
                    provenance_observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false),
                    provenance_observed_offset_minutes = table.Column<short>(type: "smallint", nullable: false),
                    provenance_confidence = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_assertions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_type = table.Column<string>(type: "text", nullable: false),
                    character = table.Column<string>(type: "text", nullable: true),
                    job = table.Column<string>(type: "text", nullable: true),
                    ordinal = table.Column<int>(type: "integer", nullable: true),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_credits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_field_locks",
                columns: table => new
                {
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_code = table.Column<string>(type: "text", nullable: false),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_field_locks", x => new { x.work_id, x.field_code });
                });

            migrationBuilder.CreateTable(
                name: "metadata_genres",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_genres", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_persons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    profile_artwork_locator = table.Column<string>(type: "text", nullable: true),
                    biography = table.Column<string>(type: "text", nullable: true),
                    birthday = table.Column<string>(type: "text", nullable: true),
                    deathday = table.Column<string>(type: "text", nullable: true),
                    place_of_birth = table.Column<string>(type: "text", nullable: true),
                    profile_observed_utc_ticks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_persons", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_refresh_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_kind = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    external_identifier_key = table.Column<string>(type: "text", nullable: false),
                    external_identifier_provider_code = table.Column<string>(type: "text", nullable: true),
                    external_identifier_namespace_code = table.Column<string>(type: "text", nullable: true),
                    external_identifier_value = table.Column<string>(type: "text", nullable: true),
                    last_attempt_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    last_attempt_offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    last_success_utc_ticks = table.Column<long>(type: "bigint", nullable: true),
                    last_success_offset_minutes = table.Column<short>(type: "smallint", nullable: true),
                    mapping_version = table.Column<string>(type: "text", nullable: false),
                    payload_fingerprint_key = table.Column<string>(type: "text", nullable: false),
                    payload_fingerprint = table.Column<string>(type: "text", nullable: true),
                    status_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_refresh_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_settings",
                columns: table => new
                {
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: true),
                    lock_item_metadata = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_settings", x => x.work_id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_studios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_studios", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "text", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    provider_code = table.Column<string>(type: "text", nullable: false),
                    observed_utc_ticks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "works",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_works", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "asset_roles",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_roles", x => new { x.asset_id, x.role_code });
                    table.ForeignKey(
                        name: "FK_asset_roles_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "technical_representations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_kind = table.Column<string>(type: "text", nullable: false),
                    codec_namespace = table.Column<string>(type: "text", nullable: false),
                    codec_code = table.Column<string>(type: "text", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    frame_rate_numerator = table.Column<int>(type: "integer", nullable: true),
                    frame_rate_denominator = table.Column<int>(type: "integer", nullable: true),
                    bit_depth = table.Column<int>(type: "integer", nullable: true),
                    bit_rate = table.Column<long>(type: "bigint", nullable: true),
                    sample_rate = table.Column<int>(type: "integer", nullable: true),
                    channel_count = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_technical_representations", x => x.id);
                    table.ForeignKey(
                        name: "FK_technical_representations_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_code = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_entities", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_entities_works_work_id",
                        column: x => x.work_id,
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "representation_source_bindings",
                columns: table => new
                {
                    representation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_namespace = table.Column<string>(type: "text", nullable: true),
                    container_code = table.Column<string>(type: "text", nullable: true),
                    stream_index = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_representation_source_bindings", x => new { x.representation_id, x.source_id });
                    table.ForeignKey(
                        name: "FK_representation_source_bindings_media_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "media_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_representation_source_bindings_technical_representations_re~",
                        column: x => x.representation_id,
                        principalTable: "technical_representations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "timelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_ticks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timelines", x => x.id);
                    table.ForeignKey(
                        name: "FK_timelines_media_entities_media_entity_id",
                        column: x => x.media_entity_id,
                        principalTable: "media_entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_coverages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_code = table.Column<string>(type: "text", nullable: false),
                    coverage_kind_code = table.Column<string>(type: "text", nullable: true),
                    alignment_offset_ticks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_coverages", x => x.id);
                    table.ForeignKey(
                        name: "FK_asset_coverages_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_coverages_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_coverages_asset_id",
                table: "asset_coverages",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_asset_coverages_timeline_id_asset_id",
                table: "asset_coverages",
                columns: new[] { "timeline_id", "asset_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_confirmed_external_identifier",
                table: "external_identifier_associations",
                columns: new[] { "provider_code", "namespace_code", "identifier_value" },
                unique: true,
                filter: "current_status_code = 'confirmed'");

            migrationBuilder.CreateIndex(
                name: "IX_external_identifier_lifecycle_entries_association_id_ordinal",
                table: "external_identifier_lifecycle_entries",
                columns: new[] { "association_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_imports_root_key_relative_path",
                table: "library_imports",
                columns: new[] { "root_key", "relative_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_entities_work_id",
                table: "media_entities",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_credits_target_id_person_id_credit_type_provider_c~",
                table: "metadata_credits",
                columns: new[] { "target_id", "person_id", "credit_type", "provider_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_genres_target_id_normalized_name_provider_code",
                table: "metadata_genres",
                columns: new[] { "target_id", "normalized_name", "provider_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_persons_provider_code_external_id",
                table: "metadata_persons",
                columns: new[] { "provider_code", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_refresh_states_target_kind_target_id_provider_code~",
                table: "metadata_refresh_states",
                columns: new[] { "target_kind", "target_id", "provider_code", "external_identifier_key", "mapping_version", "payload_fingerprint_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_studios_target_id_external_id_provider_code",
                table: "metadata_studios",
                columns: new[] { "target_id", "external_id", "provider_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metadata_tags_target_id_normalized_name_provider_code",
                table: "metadata_tags",
                columns: new[] { "target_id", "normalized_name", "provider_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_representation_source_bindings_source_id",
                table: "representation_source_bindings",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "IX_technical_representations_asset_id",
                table: "technical_representations",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_timelines_media_entity_id",
                table: "timelines",
                column: "media_entity_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artwork_references");

            migrationBuilder.DropTable(
                name: "asset_coverages");

            migrationBuilder.DropTable(
                name: "asset_roles");

            migrationBuilder.DropTable(
                name: "external_identifier_association_relations");

            migrationBuilder.DropTable(
                name: "external_identifier_associations");

            migrationBuilder.DropTable(
                name: "external_identifier_lifecycle_entries");

            migrationBuilder.DropTable(
                name: "library_imports");

            migrationBuilder.DropTable(
                name: "metadata_assertions");

            migrationBuilder.DropTable(
                name: "metadata_credits");

            migrationBuilder.DropTable(
                name: "metadata_field_locks");

            migrationBuilder.DropTable(
                name: "metadata_genres");

            migrationBuilder.DropTable(
                name: "metadata_persons");

            migrationBuilder.DropTable(
                name: "metadata_refresh_states");

            migrationBuilder.DropTable(
                name: "metadata_settings");

            migrationBuilder.DropTable(
                name: "metadata_studios");

            migrationBuilder.DropTable(
                name: "metadata_tags");

            migrationBuilder.DropTable(
                name: "representation_source_bindings");

            migrationBuilder.DropTable(
                name: "timelines");

            migrationBuilder.DropTable(
                name: "media_sources");

            migrationBuilder.DropTable(
                name: "technical_representations");

            migrationBuilder.DropTable(
                name: "media_entities");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "works");
        }
    }
}
