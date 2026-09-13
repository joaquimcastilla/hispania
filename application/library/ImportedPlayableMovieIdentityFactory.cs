using Hispania.Library.Contracts;
using Hispania.Media.Domain.Identity;

namespace Hispania.Library.Application;

public interface IImportedPlayableMovieIdentityFactory
{
    ImportedPlayableMovieIdentity Create();
}

public sealed class ImportedPlayableMovieIdentityFactory : IImportedPlayableMovieIdentityFactory
{
    public ImportedPlayableMovieIdentity Create() => new(
        WorkId.New(),
        MediaEntityId.New(),
        TimelineId.New(),
        MediaSourceId.New(),
        CreateAssetIdentity(),
        CreateAssetIdentity());

    private static ImportedAssetIdentity CreateAssetIdentity() => new(
        AssetId.New(),
        TechnicalRepresentationId.New(),
        AssetCoverageId.New());
}
