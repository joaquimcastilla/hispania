using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Hispania.Library.Persistence.EFCore;

public sealed class LibraryCatalogDbContext(DbContextOptions<LibraryCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<ImportRecord> Imports => Set<ImportRecord>();
    internal DbSet<WorkRecord> Works => Set<WorkRecord>();
    internal DbSet<MediaEntityRecord> MediaEntities => Set<MediaEntityRecord>();
    internal DbSet<TimelineRecord> Timelines => Set<TimelineRecord>();
    internal DbSet<AssetRecord> Assets => Set<AssetRecord>();
    internal DbSet<AssetRoleRecord> AssetRoles => Set<AssetRoleRecord>();
    internal DbSet<RepresentationRecord> Representations => Set<RepresentationRecord>();
    internal DbSet<SourceRecord> Sources => Set<SourceRecord>();
    internal DbSet<BindingRecord> Bindings => Set<BindingRecord>();
    internal DbSet<CoverageRecord> Coverages => Set<CoverageRecord>();
    internal DbSet<ExternalIdentifierAssociationRecord> ExternalIdentifierAssociations => Set<ExternalIdentifierAssociationRecord>();
    internal DbSet<ExternalIdentifierLifecycleRecord> ExternalIdentifierLifecycles => Set<ExternalIdentifierLifecycleRecord>();
    internal DbSet<ExternalIdentifierRelationRecord> ExternalIdentifierRelations => Set<ExternalIdentifierRelationRecord>();
    internal DbSet<MetadataAssertionRecord> MetadataAssertions => Set<MetadataAssertionRecord>();
    internal DbSet<ArtworkReferenceRecord> ArtworkReferences => Set<ArtworkReferenceRecord>();
    internal DbSet<MetadataRefreshStateRecord> MetadataRefreshStates => Set<MetadataRefreshStateRecord>();
    internal DbSet<RichGenreRecord> RichGenres => Set<RichGenreRecord>();
    internal DbSet<RichTagRecord> RichTags => Set<RichTagRecord>();
    internal DbSet<RichStudioRecord> RichStudios => Set<RichStudioRecord>();
    internal DbSet<RichPersonRecord> RichPersons => Set<RichPersonRecord>();
    internal DbSet<RichCreditRecord> RichCredits => Set<RichCreditRecord>();
    internal DbSet<MetadataSettingsRecord> MetadataSettings => Set<MetadataSettingsRecord>();
    internal DbSet<MetadataFieldLockRecord> MetadataFieldLocks => Set<MetadataFieldLockRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var guid = new ValueConverter<Guid, byte[]>(
            value => SqliteScalarConversions.ToCanonicalUuidBytes(value),
            value => SqliteScalarConversions.FromCanonicalUuidBytes(value));
        modelBuilder.Entity<ImportRecord>(entity => { entity.ToTable("library_imports"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.RootKey, x.RelativePath }).IsUnique(); });
        modelBuilder.Entity<WorkRecord>(entity => { entity.ToTable("works"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<MediaEntityRecord>(entity => { entity.ToTable("media_entities"); entity.HasKey(x => x.Id); entity.HasOne<WorkRecord>().WithMany().HasForeignKey(x => x.WorkId); });
        modelBuilder.Entity<TimelineRecord>(entity => { entity.ToTable("timelines"); entity.HasKey(x => x.Id); entity.HasOne<MediaEntityRecord>().WithMany().HasForeignKey(x => x.MediaEntityId); });
        modelBuilder.Entity<AssetRecord>(entity => { entity.ToTable("assets"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<AssetRoleRecord>(entity => { entity.ToTable("asset_roles"); entity.HasKey(x => new { x.AssetId, x.RoleCode }); entity.HasOne<AssetRecord>().WithMany().HasForeignKey(x => x.AssetId); });
        modelBuilder.Entity<RepresentationRecord>(entity => { entity.ToTable("technical_representations"); entity.HasKey(x => x.Id); entity.HasOne<AssetRecord>().WithMany().HasForeignKey(x => x.AssetId); });
        modelBuilder.Entity<SourceRecord>(entity => { entity.ToTable("media_sources"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<BindingRecord>(entity => { entity.ToTable("representation_source_bindings"); entity.HasKey(x => new { x.RepresentationId, x.SourceId }); entity.HasOne<RepresentationRecord>().WithMany().HasForeignKey(x => x.RepresentationId); entity.HasOne<SourceRecord>().WithMany().HasForeignKey(x => x.SourceId); });
        modelBuilder.Entity<CoverageRecord>(entity => { entity.ToTable("asset_coverages"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TimelineId, x.AssetId }).IsUnique(); entity.HasOne<TimelineRecord>().WithMany().HasForeignKey(x => x.TimelineId); entity.HasOne<AssetRecord>().WithMany().HasForeignKey(x => x.AssetId); });
        modelBuilder.Entity<ExternalIdentifierAssociationRecord>(entity => { entity.ToTable("external_identifier_associations"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<ExternalIdentifierLifecycleRecord>(entity => { entity.ToTable("external_identifier_lifecycle_entries"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.AssociationId, x.Ordinal }).IsUnique(); });
        modelBuilder.Entity<ExternalIdentifierRelationRecord>(entity => { entity.ToTable("external_identifier_association_relations"); entity.HasKey(x => new { x.AssociationId, x.RelatedAssociationId, x.KindCode }); });
        modelBuilder.Entity<MetadataAssertionRecord>(entity => { entity.ToTable("metadata_assertions"); entity.HasKey(x => x.Id); entity.Property(x => x.ProvenanceOriginCode).HasColumnName("provenance_origin"); });
        modelBuilder.Entity<ArtworkReferenceRecord>(entity => { entity.ToTable("artwork_references"); entity.HasKey(x => x.Id); });
        modelBuilder.Entity<MetadataRefreshStateRecord>(entity => { entity.ToTable("metadata_refresh_states"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TargetKind, x.TargetId, x.ProviderCode, x.ExternalIdentifierKey, x.MappingVersion, x.PayloadFingerprintKey }).IsUnique(); });
        modelBuilder.Entity<RichGenreRecord>(entity => { entity.ToTable("metadata_genres"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TargetId, x.NormalizedName, x.ProviderCode }).IsUnique(); });
        modelBuilder.Entity<RichTagRecord>(entity => { entity.ToTable("metadata_tags"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TargetId, x.NormalizedName, x.ProviderCode }).IsUnique(); });
        modelBuilder.Entity<RichStudioRecord>(entity => { entity.ToTable("metadata_studios"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TargetId, x.ExternalId, x.ProviderCode }).IsUnique(); });
        modelBuilder.Entity<RichPersonRecord>(entity => { entity.ToTable("metadata_persons"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.ProviderCode, x.ExternalId }).IsUnique(); });
        modelBuilder.Entity<RichCreditRecord>(entity => { entity.ToTable("metadata_credits"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.TargetId, x.PersonId, x.CreditType, x.ProviderCode }).IsUnique(); });
        modelBuilder.Entity<MetadataSettingsRecord>(entity => { entity.ToTable("metadata_settings"); entity.HasKey(x => x.WorkId); });
        modelBuilder.Entity<MetadataFieldLockRecord>(entity => { entity.ToTable("metadata_field_locks"); entity.HasKey(x => new { x.WorkId, x.FieldCode }); });
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (entity.ClrType == typeof(MetadataAssertionRecord) && property.Name == nameof(MetadataAssertionRecord.ProvenanceOriginCode))
                {
                    continue;
                }
                property.SetColumnName(ToSnakeCase(property.Name));
                if (property.ClrType == typeof(Guid)) { property.SetValueConverter(guid); property.SetColumnType("BLOB"); }
            }
        }
    }

    private static string ToSnakeCase(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
}

public static class SqliteScalarConversions
{
    public static byte[] ToCanonicalUuidBytes(Guid value) { var bytes = new byte[16]; value.TryWriteBytes(bytes, bigEndian: true, out _); return bytes; }
    public static Guid FromCanonicalUuidBytes(byte[] value) => value is { Length: 16 } ? new Guid(value, bigEndian: true) : throw new ArgumentException("A UUID BLOB must contain exactly 16 bytes.", nameof(value));
    public static (long UtcTicks, short OffsetMinutes) ToDateTimeOffset(DateTimeOffset value) => (value.UtcTicks, checked((short)value.Offset.TotalMinutes));
    public static DateTimeOffset FromDateTimeOffset(long utcTicks, short offsetMinutes) => new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc)).ToOffset(TimeSpan.FromMinutes(offsetMinutes));
    public static string ToDecimal(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static decimal FromDecimal(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ImportRecord { public Guid Id { get; set; } public string RootKey { get; set; } = null!; public string RelativePath { get; set; } = null!; public string StatusCode { get; set; } = null!; public long? ObservedLength { get; set; } public long? ObservedUtcTicks { get; set; } public short? ObservedOffsetMinutes { get; set; } public long? SuccessfulLength { get; set; } public long? SuccessfulUtcTicks { get; set; } public short? SuccessfulOffsetMinutes { get; set; } public Guid? WorkId { get; set; } public Guid? MediaEntityId { get; set; } public Guid? TimelineId { get; set; } public Guid? SourceId { get; set; } public Guid? VideoAssetId { get; set; } public Guid? VideoRepresentationId { get; set; } public Guid? VideoCoverageId { get; set; } public Guid? AudioAssetId { get; set; } public Guid? AudioRepresentationId { get; set; } public Guid? AudioCoverageId { get; set; } }
internal sealed class WorkRecord { public Guid Id { get; set; } public string CategoryCode { get; set; } = null!; }
internal sealed class MediaEntityRecord { public Guid Id { get; set; } public Guid WorkId { get; set; } public string CategoryCode { get; set; } = null!; }
internal sealed class TimelineRecord { public Guid Id { get; set; } public Guid MediaEntityId { get; set; } public long? DurationTicks { get; set; } }
internal sealed class AssetRecord { public Guid Id { get; set; } public string MediaKindCode { get; set; } = null!; }
internal sealed class AssetRoleRecord { public Guid AssetId { get; set; } public string RoleCode { get; set; } = null!; }
internal sealed class RepresentationRecord { public Guid Id { get; set; } public Guid AssetId { get; set; } public string ProfileKind { get; set; } = null!; public string CodecNamespace { get; set; } = null!; public string CodecCode { get; set; } = null!; public int? Width { get; set; } public int? Height { get; set; } public int? FrameRateNumerator { get; set; } public int? FrameRateDenominator { get; set; } public int? BitDepth { get; set; } public long? BitRate { get; set; } public int? SampleRate { get; set; } public int? ChannelCount { get; set; } }
internal sealed class SourceRecord { public Guid Id { get; set; } public string Scheme { get; set; } = null!; public string OpaqueValue { get; set; } = null!; }
internal sealed class BindingRecord { public Guid RepresentationId { get; set; } public Guid SourceId { get; set; } public string? ContainerNamespace { get; set; } public string? ContainerCode { get; set; } public int? StreamIndex { get; set; } }
internal sealed class CoverageRecord { public Guid Id { get; set; } public Guid TimelineId { get; set; } public Guid AssetId { get; set; } public string StateCode { get; set; } = null!; public string? CoverageKindCode { get; set; } public long? AlignmentOffsetTicks { get; set; } }
internal sealed class ExternalIdentifierAssociationRecord { public Guid Id { get; set; } public string ProviderCode { get; set; } = null!; public string NamespaceCode { get; set; } = null!; public string IdentifierValue { get; set; } = null!; public string CurrentStatusCode { get; set; } = null!; public string TargetKind { get; set; } = null!; public Guid TargetId { get; set; } }
internal sealed class ExternalIdentifierLifecycleRecord { public Guid Id { get; set; } public Guid AssociationId { get; set; } public int Ordinal { get; set; } public string StatusCode { get; set; } = null!; public long OccurredUtcTicks { get; set; } public short OccurredOffsetMinutes { get; set; } public string? Reason { get; set; } public string EvidenceOriginCode { get; set; } = null!; public string? EvidenceProviderCode { get; set; } public string? EvidenceIdentifierProviderCode { get; set; } public string? EvidenceIdentifierNamespaceCode { get; set; } public string? EvidenceIdentifierValue { get; set; } public long EvidenceObservedUtcTicks { get; set; } public short EvidenceObservedOffsetMinutes { get; set; } public string? EvidenceConfidence { get; set; } }
internal sealed class ExternalIdentifierRelationRecord { public Guid AssociationId { get; set; } public Guid RelatedAssociationId { get; set; } public string KindCode { get; set; } = null!; public string? Reason { get; set; } public string EvidenceOriginCode { get; set; } = null!; public string? EvidenceProviderCode { get; set; } public string? EvidenceIdentifierProviderCode { get; set; } public string? EvidenceIdentifierNamespaceCode { get; set; } public string? EvidenceIdentifierValue { get; set; } public long EvidenceObservedUtcTicks { get; set; } public short EvidenceObservedOffsetMinutes { get; set; } public string? EvidenceConfidence { get; set; } }
internal sealed class MetadataAssertionRecord { public Guid Id { get; set; } public string TargetKind { get; set; } = null!; public Guid TargetId { get; set; } public string FieldCode { get; set; } = null!; public string ValueKind { get; set; } = null!; public string? TextValue { get; set; } public long? IntegerValue { get; set; } public string? DecimalValue { get; set; } public bool? BooleanValue { get; set; } public string? DateValue { get; set; } public long? UtcTicks { get; set; } public short? OffsetMinutes { get; set; } public string StatusCode { get; set; } = null!; public Guid? SupersededByAssertionId { get; set; } public string ProvenanceOriginCode { get; set; } = null!; public string? ProvenanceProviderCode { get; set; } public string? ProvenanceIdentifierProviderCode { get; set; } public string? ProvenanceIdentifierNamespaceCode { get; set; } public string? ProvenanceIdentifierValue { get; set; } public long ProvenanceObservedUtcTicks { get; set; } public short ProvenanceObservedOffsetMinutes { get; set; } public string? ProvenanceConfidence { get; set; } }
internal sealed class ArtworkReferenceRecord { public Guid Id { get; set; } public string TargetKind { get; set; } = null!; public Guid TargetId { get; set; } public string RoleCode { get; set; } = null!; public string LocatorScheme { get; set; } = null!; public string LocatorValue { get; set; } = null!; public string ProvenanceOriginCode { get; set; } = null!; public string? ProvenanceProviderCode { get; set; } public string? ProvenanceIdentifierProviderCode { get; set; } public string? ProvenanceIdentifierNamespaceCode { get; set; } public string? ProvenanceIdentifierValue { get; set; } public long ProvenanceObservedUtcTicks { get; set; } public short ProvenanceObservedOffsetMinutes { get; set; } public string? ProvenanceConfidence { get; set; } }
internal sealed class MetadataRefreshStateRecord { public Guid Id { get; set; } public string TargetKind { get; set; } = null!; public Guid TargetId { get; set; } public string ProviderCode { get; set; } = null!; public string ExternalIdentifierKey { get; set; } = null!; public string? ExternalIdentifierProviderCode { get; set; } public string? ExternalIdentifierNamespaceCode { get; set; } public string? ExternalIdentifierValue { get; set; } public long? LastAttemptUtcTicks { get; set; } public short? LastAttemptOffsetMinutes { get; set; } public long? LastSuccessUtcTicks { get; set; } public short? LastSuccessOffsetMinutes { get; set; } public string MappingVersion { get; set; } = null!; public string PayloadFingerprintKey { get; set; } = null!; public string? PayloadFingerprint { get; set; } public string StatusCode { get; set; } = null!; }
internal sealed class RichGenreRecord { public Guid Id { get; set; } public Guid TargetId { get; set; } public string Name { get; set; } = null!; public string NormalizedName { get; set; } = null!; public int Ordinal { get; set; } public string ProviderCode { get; set; } = null!; public long ObservedUtcTicks { get; set; } }
internal sealed class RichTagRecord { public Guid Id { get; set; } public Guid TargetId { get; set; } public string Name { get; set; } = null!; public string NormalizedName { get; set; } = null!; public int Ordinal { get; set; } public string ProviderCode { get; set; } = null!; public long ObservedUtcTicks { get; set; } }
internal sealed class RichStudioRecord { public Guid Id { get; set; } public Guid TargetId { get; set; } public string ExternalId { get; set; } = null!; public string Name { get; set; } = null!; public int Ordinal { get; set; } public string ProviderCode { get; set; } = null!; public long ObservedUtcTicks { get; set; } }
internal sealed class RichPersonRecord { public Guid Id { get; set; } public string ProviderCode { get; set; } = null!; public string ExternalId { get; set; } = null!; public string Name { get; set; } = null!; public string? ProfileArtworkLocator { get; set; } public string? Biography { get; set; } public string? Birthday { get; set; } public string? Deathday { get; set; } public string? PlaceOfBirth { get; set; } public long? ProfileObservedUtcTicks { get; set; } }
internal sealed class RichCreditRecord { public Guid Id { get; set; } public Guid TargetId { get; set; } public Guid PersonId { get; set; } public string CreditType { get; set; } = null!; public string? Character { get; set; } public string? Job { get; set; } public int? Ordinal { get; set; } public string ProviderCode { get; set; } = null!; public long ObservedUtcTicks { get; set; } }
internal sealed class MetadataSettingsRecord { public Guid WorkId { get; set; } public string? Language { get; set; } public string? Region { get; set; } public bool LockItemMetadata { get; set; } }
internal sealed class MetadataFieldLockRecord { public Guid WorkId { get; set; } public string FieldCode { get; set; } = null!; public bool IsLocked { get; set; } }
