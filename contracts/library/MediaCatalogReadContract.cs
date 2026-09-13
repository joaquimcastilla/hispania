using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;

namespace Hispania.Library.Contracts;

public sealed record LibrarySourceSnapshot(long? ByteLength, DateTimeOffset? LastModifiedUtc);

public sealed record ImportedMedia(
    Work Work,
    MediaEntity MediaEntity,
    Timeline Timeline,
    Asset VideoAsset,
    Asset AudioAsset,
    MediaSource Source);

public sealed record MediaCatalogEntry(
    MediaEntityId Id,
    string DisplayName,
    LibraryFileReference File,
    LibrarySourceSnapshot Snapshot,
    ImportedMedia Media);

public interface IMediaCatalogReader
{
    IReadOnlyCollection<MediaCatalogEntry> GetAll();

    MediaCatalogEntry? GetById(MediaEntityId id);
}
