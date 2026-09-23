using Hispania.Metadata.Contracts;
using Hispania.Media.Domain.Enrichment;

namespace Hispania.Metadata.Application;

public sealed class EffectiveRichMetadataResolver
{
    public static IReadOnlyCollection<T> Resolve<T>(IReadOnlyCollection<RichMetadataSourceSnapshot> sources, RichMetadataCollectionType type)
    {
        var manual = sources.Where(x => x.Origin == MetadataProvenanceOrigin.Manual).OrderByDescending(x => x.ObservedAt).FirstOrDefault();
        if (manual is not null) return Values<T>(manual, type);
        var source = sources.Where(x => x.Origin != MetadataProvenanceOrigin.Manual)
            .OrderByDescending(x => x.ObservedAt).ThenBy(x => x.ProviderCode ?? "", StringComparer.Ordinal).FirstOrDefault();
        return source is null ? [] : Values<T>(source, type);
    }

    private static IReadOnlyCollection<T> Values<T>(RichMetadataSourceSnapshot source, RichMetadataCollectionType type) => type switch
    {
        RichMetadataCollectionType.Genres when typeof(T) == typeof(string) => (IReadOnlyCollection<T>)source.Genres,
        RichMetadataCollectionType.Tags when typeof(T) == typeof(string) => (IReadOnlyCollection<T>)source.Tags,
        RichMetadataCollectionType.Studios when typeof(T) == typeof(NormalizedStudio) => (IReadOnlyCollection<T>)source.Studios,
        _ => []
    };
}
