using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Hispania.Library.Persistence.EFCore;

public sealed class EfMetadataSettingsStore(IDbContextFactory<LibraryCatalogDbContext> factory) : IMetadataSettingsStore
{
    public async ValueTask<MetadataSettings> GetSettingsAsync(WorkId workId, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); var row = await db.MetadataSettings.SingleOrDefaultAsync(x => x.WorkId == workId.Value, cancellationToken); return row is null ? new(workId, null, null, false) : new(workId, row.Language, row.Region, row.LockItemMetadata); }
    public async ValueTask SetSettingsAsync(MetadataSettings settings, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); var row = await db.MetadataSettings.SingleOrDefaultAsync(x => x.WorkId == settings.WorkId.Value, cancellationToken); if (row is null) db.MetadataSettings.Add(new MetadataSettingsRecord { WorkId = settings.WorkId.Value, Language = settings.PreferredMetadataLanguage, Region = settings.MetadataCountryRegion, LockItemMetadata = settings.LockItemMetadata }); else { row.Language = settings.PreferredMetadataLanguage; row.Region = settings.MetadataCountryRegion; row.LockItemMetadata = settings.LockItemMetadata; } await db.SaveChangesAsync(cancellationToken); }
    public async ValueTask<IReadOnlyCollection<MetadataFieldLock>> GetLocksAsync(WorkId workId, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); return await db.MetadataFieldLocks.AsNoTracking().Where(x => x.WorkId == workId.Value && x.IsLocked).Select(x => new MetadataFieldLock(workId, x.FieldCode, x.IsLocked)).ToArrayAsync(cancellationToken); }
    public async ValueTask SetLockAsync(MetadataFieldLock fieldLock, CancellationToken cancellationToken = default)
    { await using var db = await factory.CreateDbContextAsync(cancellationToken); var row = await db.MetadataFieldLocks.SingleOrDefaultAsync(x => x.WorkId == fieldLock.WorkId.Value && x.FieldCode == fieldLock.FieldCode, cancellationToken); if (row is null) db.MetadataFieldLocks.Add(new MetadataFieldLockRecord { WorkId = fieldLock.WorkId.Value, FieldCode = fieldLock.FieldCode, IsLocked = fieldLock.IsLocked }); else row.IsLocked = fieldLock.IsLocked; await db.SaveChangesAsync(cancellationToken); }
}
