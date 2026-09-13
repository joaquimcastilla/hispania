using Hispania.Library.Application;
using Hispania.Library.Persistence.EFCore;
using Hispania.Library.Persistence.Sqlite;

namespace Hispania.Server;

public sealed class LibraryOptions
{
    public List<LibraryRootOptions> Roots { get; init; } = [];
}

public sealed class LibraryRootOptions
{
    public string Key { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

public sealed class LibraryReadiness
{
    public bool IsReady { get; private set; }
    public void MarkReady() => IsReady = true;
}

public sealed class PersistenceStartupService(SqliteMigrationRunner migrationRunner, EfLibraryCatalogStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => migrationRunner.MigrateAndLoadAsync(store, cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InitialLibraryScanService(LibraryScanner scanner, LibraryReadiness readiness) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await scanner.ScanAsync(cancellationToken);
        readiness.MarkReady();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
