using Hispania.Library.Contracts;
using Hispania.Library.Persistence.EFCore;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hispania.Library.Persistence.PostgreSql;

public static class PostgreSqlLibraryPersistence
{
    public static IServiceCollection AddPostgreSqlLibraryPersistence(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContextFactory<LibraryCatalogDbContext>(options => options.UseNpgsql(connectionString,
            postgres => postgres.MigrationsAssembly(typeof(PostgreSqlLibraryPersistence).Assembly.FullName)));
        services.AddSingleton<IUniqueConstraintViolationClassifier, PostgreSqlUniqueConstraintViolationClassifier>();
        services.AddSingleton<EfLibraryCatalogStore>();
        services.AddSingleton<ILibraryCatalogStore>(provider => provider.GetRequiredService<EfLibraryCatalogStore>());
        services.AddSingleton<IMediaCatalogReader>(provider => provider.GetRequiredService<EfLibraryCatalogStore>());
        services.AddSingleton<EfMetadataEnrichmentStore>();
        services.AddSingleton<IMetadataEnrichmentStore>(provider => provider.GetRequiredService<EfMetadataEnrichmentStore>());
        services.AddSingleton<EfRichMetadataStore>();
        services.AddSingleton<IRichMetadataStore>(provider => provider.GetRequiredService<EfRichMetadataStore>());
        services.AddSingleton<EfMetadataSettingsStore>();
        services.AddSingleton<IMetadataSettingsStore>(provider => provider.GetRequiredService<EfMetadataSettingsStore>());
        services.AddSingleton<PostgreSqlMigrationRunner>();
        services.AddSingleton<ILibraryPersistenceInitializer>(provider => provider.GetRequiredService<PostgreSqlMigrationRunner>());
        return services;
    }
}

internal sealed class PostgreSqlUniqueConstraintViolationClassifier : IUniqueConstraintViolationClassifier
{
    public bool IsConfirmedExternalIdentifierConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgres.ConstraintName == "ux_confirmed_external_identifier";
}

public sealed class PostgreSqlMigrationRunner(IDbContextFactory<LibraryCatalogDbContext> contextFactory) : ILibraryPersistenceInitializer
{
    public static void ValidateBackupHistory(string history)
    {
        if (history != "20260926104649_InitialPostgreSql") throw new InvalidDataException("Unsupported PostgreSQL migration lineage.");
    }
    public static void ValidateBackupMetadata(string? metadata)
    {
        if (metadata is null) throw new InvalidDataException("Missing PostgreSQL schema metadata.");
        var separator = metadata.IndexOf('|');
        if (separator < 0 || !metadata[(separator + 1)..].StartsWith("pg_dump ", StringComparison.Ordinal)) throw new InvalidDataException("Unsupported PostgreSQL schema metadata.");
        ValidateBackupHistory(metadata[..separator]);
    }

    public async Task InitializeAsync(EfLibraryCatalogStore store, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await Hispania.Persistence.Schema.EfSchemaGuard.UpgradeAsync(context, "Media.Persistence.PostgreSQL", cancellationToken);
        await store.LoadSnapshotAsync(cancellationToken: cancellationToken);
    }
}
