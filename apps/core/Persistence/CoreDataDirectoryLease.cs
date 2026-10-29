using Microsoft.Extensions.Hosting;
using Hispania.Core.Configuration;
using Hispania.Runtime;

namespace Hispania.Core.Persistence;

internal sealed class CoreDataDirectoryLease : IDisposable
{
    private const string LockFileName = ".core-state-operation.lock";
    private readonly FileStream _stream;

    private CoreDataDirectoryLease(FileStream stream) => _stream = stream;

    public static CoreDataDirectoryLease Acquire(string dataDirectory, bool exclusive)
    {
        StoragePathGuard.EnsureNoReparsePoints(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        StoragePathGuard.EnsureNoReparsePoints(dataDirectory);
        var lockPath = Path.Combine(dataDirectory, LockFileName);
        StoragePathGuard.EnsureNoReparsePoints(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                exclusive ? FileShare.None : FileShare.ReadWrite, bufferSize: 1, FileOptions.None);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new CoreDataDirectoryLease(stream);
        }
        catch (IOException)
        {
            throw new CoreStateBackupException(exclusive
                ? "Core state cannot be restored while a Core process or another operation is active."
                : "Core state operation could not acquire the data directory lease.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new CoreStateBackupException("Core state operation is not permitted for the configured data directory.");
        }
    }

    public void Dispose() => _stream.Dispose();
}

internal sealed class CoreDataDirectoryLeaseHostedService(CoreStorageOptions options) : IHostedLifecycleService, IDisposable
{
    private CoreDataDirectoryLease? _lease;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lease = CoreDataDirectoryLease.Acquire(options.DataDirectory, exclusive: false);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lease?.Dispose();
        _lease = null;
    }
}

internal sealed class CoreStateBackupException(string message, Exception? innerException = null) : Exception(message, innerException);
