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
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
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
