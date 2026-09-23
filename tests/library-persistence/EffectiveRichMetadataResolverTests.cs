using Hispania.Media.Domain.Enrichment;
using Hispania.Metadata.Application;
using Hispania.Metadata.Contracts;
using Xunit;

namespace Hispania.Library.Persistence.Sqlite.Tests;

public sealed class EffectiveRichMetadataResolverTests
{
    [Fact]
    public void ManualCollectionWinsWithoutUnion()
    {
        var sources = new RichMetadataSourceSnapshot[]
        {
            Source("tmdb", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), ["Action"]),
            Source("other", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), ["Crime"]),
            new(MetadataProvenanceOrigin.Manual, null, new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), ["Thriller"], [], [], [], [])
        };
        Assert.Equal(["Thriller"], EffectiveRichMetadataResolver.Resolve<string>(sources, RichMetadataCollectionType.Genres));
    }

    [Fact]
    public void ExternalResolutionUsesObservedAtThenProviderCode()
    {
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var sources = new[] { Source("tmdb", at, ["TMDB"]), Source("alpha-provider", at, ["Alpha"]) };
        Assert.Equal(["Alpha"], EffectiveRichMetadataResolver.Resolve<string>(sources, RichMetadataCollectionType.Genres));
    }

    private static RichMetadataSourceSnapshot Source(string provider, DateTimeOffset observedAt, IReadOnlyCollection<string> genres) => new(MetadataProvenanceOrigin.ExternalProvider, provider, observedAt, genres, [], [], [], []);
}
