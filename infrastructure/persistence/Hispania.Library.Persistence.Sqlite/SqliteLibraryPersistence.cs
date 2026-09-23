using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Data;
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
        services.AddSingleton<EfRichMetadataStore>();
        services.AddSingleton<IRichMetadataStore>(provider => provider.GetRequiredService<EfRichMetadataStore>());
        services.AddSingleton<EfMetadataSettingsStore>();
        services.AddSingleton<IMetadataSettingsStore>(provider => provider.GetRequiredService<EfMetadataSettingsStore>());
        services.AddSingleton<SqliteMigrationRunner>();
        return services;
    }
}

public sealed class SqliteMigrationRunner(IDbContextFactory<LibraryCatalogDbContext> contextFactory)
{
    public async Task MigrateAndLoadAsync(EfLibraryCatalogStore store, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await NormalizeLegacyMigrationHistoryAsync(context, cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        await store.LoadSnapshotAsync(cancellationToken: cancellationToken);
    }

    private static async Task NormalizeLegacyMigrationHistoryAsync(LibraryCatalogDbContext context, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand(); exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory' LIMIT 1";
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return;
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0001_initial"] = "20260922000100_initial",
            ["0002_metadata_enrichment"] = "20260922000200_metadata_enrichment",
            ["0003_rich_metadata"] = "20260922000300_rich_metadata",
            ["0004_metadata_settings"] = "20260922000400_metadata_settings",
            ["0005_person_metadata"] = "20260923000500_person_metadata"
        };
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        foreach (var pair in mapping)
        {
            await using var conflict = connection.CreateCommand(); conflict.Transaction = transaction; conflict.CommandText = "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId IN ($legacy, $standard)"; conflict.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$legacy", pair.Key)); conflict.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$standard", pair.Value));
            var count = Convert.ToInt32(await conflict.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 1) throw new InvalidOperationException($"Migration history conflict: both '{pair.Key}' and '{pair.Value}' exist.");
            if (count == 1)
            {
                await using var update = connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE __EFMigrationsHistory SET MigrationId = $standard WHERE MigrationId = $legacy"; update.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$legacy", pair.Key)); update.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$standard", pair.Value)); await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
