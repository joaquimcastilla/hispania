using System.Text.RegularExpressions;
using Hispania.Platform.Contracts.Storage;
using Hispania.Platform.Contracts.Maintenance;
using Hispania.Platform.Storage.Application;

namespace Hispania.Platform.Storage.Filesystem;

public sealed class FilesystemStorageProviderV1 : IStorageProviderV1, IStorageInventoryV1
{
    private const int BufferSize = 81920;
    private static readonly Regex IdPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _root;
    private readonly ICapacitySourceV1? _capacity;
    private readonly CapacityThresholdsV1? _thresholds;

    public FilesystemStorageProviderV1(string root, ICapacitySourceV1? capacity = null,
        CapacityThresholdsV1? thresholds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        if ((capacity is null) != (thresholds is null))
            throw new ArgumentException("Capacity source and thresholds must be configured together.");
        thresholds?.Validate();
        _capacity = capacity;
        _thresholds = thresholds;
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Filesystem storage root cannot be a symbolic link or reparse point.");
    }

    public string ProviderId => "filesystem";

    public ValueTask<StorageInventoryPageV1> ScanAsync(int maximumItems, string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (continuationToken is not null && !IsBlobName(continuationToken))
            throw new ArgumentException("Storage inventory cursor is malformed.", nameof(continuationToken));
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Filesystem storage root cannot be a symbolic link or reparse point.");
        var selected = new SortedDictionary<string, FileInfo>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(_root, "*.blob", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!IsBlobName(name) || continuationToken is not null &&
                string.Compare(name, continuationToken, StringComparison.Ordinal) <= 0) continue;
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                throw new IOException("Storage inventory encountered a linked object.");
            selected.Add(name, info);
            if (selected.Count > maximumItems + 1) selected.Remove(selected.Keys.Last());
        }
        var complete = selected.Count <= maximumItems;
        var page = new List<StorageInventoryObjectV1>(Math.Min(selected.Count, maximumItems));
        foreach (var (name, info) in selected.Take(maximumItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var id = name[..32];
                var handle = id.All(c => !char.IsAsciiLetterUpper(c)) ? $"storage://filesystem/{id}" : id.ToUpperInvariant();
                page.Add(new(new StorageObjectHandle(handle),
                    info.Length, info.LastWriteTimeUtc));
            }
            catch (FileNotFoundException) { }
        }
        var next = complete || page.Count == 0 ? null : selected.Keys.ElementAt(maximumItems - 1);
        return ValueTask.FromResult(new StorageInventoryPageV1(page, next, complete));
    }

    private static bool IsBlobName(string? name) => name is { Length: 37 } &&
        name.EndsWith(".blob", StringComparison.Ordinal) && name[..32].All(Uri.IsHexDigit);

    public async ValueTask<(StorageObjectHandle Handle, StorageObjectMetadata Metadata)> WriteAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (_capacity is not null && _thresholds is not null)
        {
            var requested = content.CanSeek ? Math.Min(maximumBytes, Math.Max(0, content.Length - content.Position)) : maximumBytes;
            var snapshot = await _capacity.ObserveAsync(cancellationToken).ConfigureAwait(false);
            if (!CapacityPolicyV1.CanReserve(snapshot, requested, _thresholds))
                throw new IOException("Storage capacity preflight refused this write.");
        }
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
