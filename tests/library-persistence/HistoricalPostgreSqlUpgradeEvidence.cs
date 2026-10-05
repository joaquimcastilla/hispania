using System.Security.Cryptography;
using System.Text.Json;
using Hispania.Library.Persistence.PostgreSql;
using Hispania.Library.Persistence.EFCore;
using Hispania.Media.Domain.Identity;
using Hispania.Metadata.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
namespace Hispania.Library.Persistence.Sqlite.Tests;
internal static class HistoricalPostgreSqlUpgradeEvidence
{
    internal static async Task RunAsync(string connectionString)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null && !File.Exists(Path.Combine(directory.FullName,"Hispania.sln")))directory=directory.Parent;
        var compatibility=Path.Combine(directory!.FullName,"compatibility");
        var manifestBytes=File.ReadAllBytes(Path.Combine(compatibility,"data-baseline-0.7.json"));
        Assert.Equal("68a28dafbf9709284b361dacf8113219ac44d130d0ca77a7646e6df29c7bc898",Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
        using var manifest=JsonDocument.Parse(manifestBytes);
        foreach(var row in manifest.RootElement.GetProperty("artifacts").EnumerateArray().Where(r=>r.GetProperty("path").GetString()!.EndsWith(".sql",StringComparison.Ordinal)))
            Assert.Equal(row.GetProperty("sha256").GetString(),Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(compatibility,row.GetProperty("path").GetString()!)))).ToLowerInvariant());
        var schema="hispania_historical_"+Guid.NewGuid().ToString("N");
        await using var admin=new NpgsqlConnection(connectionString);await admin.OpenAsync();
        await using(var create=admin.CreateCommand()){create.CommandText=$"CREATE SCHEMA \"{schema}\"";await create.ExecuteNonQueryAsync();}
        try
        {
            var isolated=new NpgsqlConnectionStringBuilder(connectionString){SearchPath=schema,Pooling=false}.ConnectionString;
            await using(var old=new NpgsqlConnection(isolated))
            {
                await old.OpenAsync();
                foreach(var file in new[]{"media-postgresql-schema.sql","media-postgresql-data.sql"})
                {await using var q=old.CreateCommand();q.CommandText=File.ReadAllText(Path.Combine(compatibility,"data","0.7",file));await q.ExecuteNonQueryAsync();}
            }
            await using var provider=new ServiceCollection().AddPostgreSqlLibraryPersistence(isolated).BuildServiceProvider();
            var runner=provider.GetRequiredService<PostgreSqlMigrationRunner>();var store=provider.GetRequiredService<EfLibraryCatalogStore>();
            await runner.InitializeAsync(store);await runner.InitializeAsync(store);
            var factory=provider.GetRequiredService<IDbContextFactory<LibraryCatalogDbContext>>();await using var current=await factory.CreateDbContextAsync();
            Assert.Equal("20260926104649_InitialPostgreSql",Assert.Single(await current.Database.GetAppliedMigrationsAsync()));
            Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000047"),(await current.Works.SingleAsync()).Id);
            Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000048"),(await current.MediaEntities.SingleAsync()).Id);
            var settings=await provider.GetRequiredService<IMetadataSettingsStore>().GetSettingsAsync(new WorkId(Guid.Parse("00000000-0000-0000-0000-000000000047")));
            Assert.Equal("es",settings.PreferredMetadataLanguage);Assert.Equal("ES",settings.MetadataCountryRegion);Assert.True(settings.LockItemMetadata);
            // Enforce constraints on the populated historical schema, not only on a fresh provider DB.
            var duplicate=await Assert.ThrowsAsync<PostgresException>(()=>current.Database.ExecuteSqlRawAsync("INSERT INTO works(id,category_code) VALUES ('00000000-0000-0000-0000-000000000047','movie')"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation,duplicate.SqlState);
            var missingWork=await Assert.ThrowsAsync<PostgresException>(()=>current.Database.ExecuteSqlRawAsync("INSERT INTO media_entities(id,work_id,category_code) VALUES ('00000000-0000-0000-0000-000000000099','00000000-0000-0000-0000-000000000098','movie')"));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,missingWork.SqlState);
            Assert.Single(await current.Works.ToListAsync());Assert.Single(await current.MediaEntities.ToListAsync());
            // Provider serialization/locking: both startup paths preserve the single EF lineage.
            await Task.WhenAll(runner.InitializeAsync(store),runner.InitializeAsync(store));
            await current.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('20990101000100_future','10.0.12')");
            await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.InitializeAsync(store));Assert.Single(await current.Works.ToListAsync());
            Assert.Equal(2,(await current.Database.GetAppliedMigrationsAsync()).Count());
            Console.WriteLine("Phase16 PostgreSQL: frozen old schema/data -> current EF runner -> IDs/settings and historical PK/FK preserved; repeat/concurrent startup and future-schema guard passed.");
        }
        finally
        { await using var drop=admin.CreateCommand();drop.CommandText=$"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";await drop.ExecuteNonQueryAsync(); }
    }
}
