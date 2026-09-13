using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Library.Persistence.Sqlite;

public static class SqliteLibraryPersistence
{
    public static IServiceCollection AddSqliteLibraryPersistence(this IServiceCollection services, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        services.AddDbContextFactory<LibraryCatalogDbContext>(options => options.UseSqlite($"Data Source={path}", sqlite => sqlite.MigrationsAssembly(typeof(SqliteLibraryPersistence).Assembly.FullName)));
        services.AddSingleton<EfLibraryCatalogStore>();
        services.AddSingleton<ILibraryCatalogStore>(provider => provider.GetRequiredService<EfLibraryCatalogStore>());
        services.AddSingleton<IMediaCatalogReader>(provider => provider.GetRequiredService<EfLibraryCatalogStore>());
        services.AddSingleton<EfMetadataEnrichmentStore>();
        services.AddSingleton<IMetadataEnrichmentStore>(provider => provider.GetRequiredService<EfMetadataEnrichmentStore>());
        services.AddSingleton<SqliteMigrationRunner>();
        return services;
    }
}

public sealed class SqliteMigrationRunner(IDbContextFactory<LibraryCatalogDbContext> contextFactory)
{
    public async Task MigrateAndLoadAsync(EfLibraryCatalogStore store, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        await store.LoadSnapshotAsync(cancellationToken);
    }
}
