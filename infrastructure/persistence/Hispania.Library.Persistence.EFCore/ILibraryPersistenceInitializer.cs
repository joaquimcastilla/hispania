using Hispania.Library.Contracts;

namespace Hispania.Library.Persistence.EFCore;

public interface ILibraryPersistenceInitializer
{
    Task InitializeAsync(EfLibraryCatalogStore store, CancellationToken cancellationToken = default);
}
