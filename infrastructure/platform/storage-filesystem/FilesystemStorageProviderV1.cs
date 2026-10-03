using System.Text.RegularExpressions;
using Hispania.Platform.Contracts.Storage;

namespace Hispania.Platform.Storage.Filesystem;

public sealed class FilesystemStorageProviderV1 : IStorageProviderV1
{
    private const int BufferSize = 81920;
    private static readonly Regex IdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _root;

    public FilesystemStorageProviderV1(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string ProviderId => "filesystem";

    public async ValueTask<(StorageObjectHandle Handle, StorageObjectMetadata Metadata)> WriteAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var id = Guid.NewGuid().ToString("N");
        var target = Path.Combine(_root, id + ".blob");
        var temporary = Path.Combine(_root, "." + id + "." + Guid.NewGuid().ToString("N") + ".partial");
        long total = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                TryRestrict(temporary);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total = checked(total + read);
                    if (total > maximumBytes) throw new InvalidDataException("Asset exceeds the configured size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, target);
            return (new StorageObjectHandle($"storage://filesystem/{id}"), new StorageObjectMetadata(total, null, DateTimeOffset.UtcNow));
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    public ValueTask<Stream?> OpenReadAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(handle);
        if (!File.Exists(path)) return ValueTask.FromResult<Stream?>(null);
        try
        {
            RejectLink(path);
            return ValueTask.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (FileNotFoundException) { return ValueTask.FromResult<Stream?>(null); }
        catch (DirectoryNotFoundException) { return ValueTask.FromResult<Stream?>(null); }
    }

    public ValueTask<bool> DeleteAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(handle);
        if (!File.Exists(path)) return ValueTask.FromResult(false);
        try { RejectLink(path); File.Delete(path); return ValueTask.FromResult(true); }
        catch (FileNotFoundException) { return ValueTask.FromResult(false); }
        catch (DirectoryNotFoundException) { return ValueTask.FromResult(false); }
    }

    public ValueTask<bool> ExistsAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(ResolvePath(handle)));
    }

    public async ValueTask<StorageObjectMetadata?> GetMetadataAsync(StorageObjectHandle handle, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(handle);
        if (!File.Exists(path)) return null;
        RejectLink(path);
        var info = new FileInfo(path);
        cancellationToken.ThrowIfCancellationRequested();
        return new StorageObjectMetadata(info.Length, null, info.CreationTimeUtc);
    }

    public ValueTask<StorageProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new StorageProviderHealth(Directory.Exists(_root), Directory.Exists(_root) ? "Filesystem storage root is accessible." : "Filesystem storage root is unavailable."));
    }

    private string ResolvePath(StorageObjectHandle handle)
    {
        if (handle.Value.StartsWith("storage://filesystem/", StringComparison.Ordinal))
        {
            var id = handle.Value["storage://filesystem/".Length..];
            if (!IdPattern.IsMatch(id)) throw new ArgumentException("Storage handle is malformed.", nameof(handle));
            return Path.Combine(_root, id + ".blob");
        }

        // Read/delete compatibility for the original Platform Assets hex handles.
        if (handle.Value.Length == 32 && handle.Value.All(Uri.IsHexDigit))
            return Path.Combine(_root, handle.Value.ToUpperInvariant() + ".blob");
        throw new ArgumentException("Storage handle is malformed.", nameof(handle));
    }

    private static void TryRestrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (PlatformNotSupportedException) { }
    }

    private static void RejectLink(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new IOException("Filesystem storage object cannot be a symbolic link or reparse point.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
