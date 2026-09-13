using Hispania.Library.Contracts;
using Hispania.Media.Domain.Assets;
using Hispania.Media.Domain.Identity;
using Hispania.Media.Domain.MediaEntities;
using Hispania.Media.Domain.Sources;
using Hispania.Media.Domain.Timelines;
using Hispania.Media.Domain.Works;

namespace Hispania.Library.Application;

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
    ImportedMedia Media);

public interface IMediaCatalog
{
    IReadOnlyCollection<MediaCatalogEntry> GetAll();

    MediaCatalogEntry? GetById(MediaEntityId id);

    void Upsert(MediaCatalogEntry entry);

    void Replace(IEnumerable<MediaCatalogEntry> entries);
}

public sealed class InMemoryMediaCatalog : IMediaCatalog
{
    private Dictionary<MediaEntityId, MediaCatalogEntry> _entries = [];

    public IReadOnlyCollection<MediaCatalogEntry> GetAll() => _entries.Values.ToArray();

    public MediaCatalogEntry? GetById(MediaEntityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _entries.GetValueOrDefault(id);
    }

    public void Upsert(MediaCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.Id] = entry;
    }

    public void Replace(IEnumerable<MediaCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var replacement = new Dictionary<MediaEntityId, MediaCatalogEntry>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                throw new ArgumentException("A media catalog cannot contain null entries.", nameof(entries));
            }

            if (!replacement.TryAdd(entry.Id, entry))
            {
                throw new ArgumentException("A media catalog cannot contain duplicate MediaEntity IDs.", nameof(entries));
            }
        }

        _entries = replacement;
    }
}
