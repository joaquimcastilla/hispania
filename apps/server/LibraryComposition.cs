using Hispania.Library.Application;

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

public sealed class InitialLibraryScanService(LibraryScanner scanner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => scanner.ScanAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
