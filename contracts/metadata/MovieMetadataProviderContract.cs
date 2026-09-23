using Hispania.Media.Domain.Enrichment;

namespace Hispania.Metadata.Contracts;

public sealed record MovieMetadataProviderDescriptor(ExternalIdentifierProvider ProviderCode, string DisplayName, IReadOnlyCollection<ExternalIdentifierNamespace> SupportedNamespaces, string MappingVersion);
public sealed record MovieSearchRequest(string TitleCandidate, int? Year, TimeSpan? ObservedDuration);
public sealed record MovieSearchCandidate(ExternalIdentifier ExternalIdentifier, string Title, string? OriginalTitle, DateOnly? ReleaseDate, decimal? ProviderScore, decimal? ProviderConfidence, string? Explanation);
public sealed record NormalizedMetadataField(MetadataField Field, MetadataValue Value, decimal? Confidence);
public sealed record NormalizedArtworkReference(ArtworkRole Role, ArtworkLocator Locator, decimal? Confidence);
public sealed record NormalizedPerson(string ProviderId, string Name, string? ProfileArtworkLocator = null, string? ProviderCode = null, Guid? InternalPersonId = null, string? Biography = null, DateOnly? Birthday = null, DateOnly? Deathday = null, string? PlaceOfBirth = null);
public interface IPersonMetadataProvider
{
    ValueTask<NormalizedPerson?> GetPersonAsync(string providerPersonId, CancellationToken cancellationToken = default);
}
public sealed record NormalizedCredit(string ProviderPersonId, string PersonName, string CreditType, string? Character, string? Job, int? Ordinal, string? ProfileArtworkLocator = null);
public sealed record NormalizedStudio(string ProviderId, string Name);
public sealed record NormalizedMovieRichMetadata(IReadOnlyCollection<string> Genres, IReadOnlyCollection<string> Tags, IReadOnlyCollection<NormalizedStudio> Studios, IReadOnlyCollection<NormalizedPerson> People, IReadOnlyCollection<NormalizedCredit> Credits);
public sealed record NormalizedMovieDocument(ExternalIdentifier PrimaryExternalIdentifier, IReadOnlyCollection<ExternalIdentifier> SecondaryExternalIdentifiers, IReadOnlyCollection<NormalizedMetadataField> Fields, IReadOnlyCollection<NormalizedArtworkReference> Artwork, DateTimeOffset ObservedAt, string MappingVersion, string PayloadFingerprint, NormalizedMovieRichMetadata? RichMetadata = null);
public abstract record MovieProviderResult;
public sealed record MovieProviderSuccess<T>(T Value) : MovieProviderResult;
public sealed record MovieProviderUnavailable(string Detail) : MovieProviderResult;
public sealed record MovieProviderTimeout(string Detail) : MovieProviderResult;
public sealed record MovieProviderInvalidResponse(string Detail) : MovieProviderResult;
public sealed record MovieProviderUnsupported(string Detail) : MovieProviderResult;
public sealed record MovieProviderCancelled : MovieProviderResult;
public interface IMovieMetadataProvider
{
    MovieMetadataProviderDescriptor Descriptor { get; }
    ValueTask<MovieProviderResult> SearchAsync(MovieSearchRequest request, CancellationToken cancellationToken = default);
    ValueTask<MovieProviderResult> GetMovieAsync(ExternalIdentifier identifier, CancellationToken cancellationToken = default);
}
