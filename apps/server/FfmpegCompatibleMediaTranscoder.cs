using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Hispania.Library.Contracts;
using Hispania.Playback.Contracts;
using Hispania.Media.Domain.Identity;

namespace Hispania.Server;

public interface ICompatibleMediaTranscoder
{
    ValueTask<string?> GetMp4FileAsync(MediaEntityId mediaEntityId, CancellationToken cancellationToken = default);
}

/// <summary>Creates a cached, browser-compatible MP4 derivative without changing the library source file.</summary>
public sealed class FfmpegCompatibleMediaTranscoder : ICompatibleMediaTranscoder
{
    private const long MaximumCacheBytes = 20L * 1024 * 1024 * 1024;
    private readonly IMediaCatalogReader catalog;
    private readonly ISourceContentAccessor sourceAccessor;
    private readonly string executablePath;
    private readonly string cacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    public FfmpegCompatibleMediaTranscoder(IMediaCatalogReader catalog, ISourceContentAccessor sourceAccessor, string executablePath, string cacheDirectory)
    {
        this.catalog = catalog;
        this.sourceAccessor = sourceAccessor;
        this.executablePath = executablePath;
        this.cacheDirectory = cacheDirectory;
    }

    public async ValueTask<string?> GetMp4FileAsync(MediaEntityId mediaEntityId, CancellationToken cancellationToken = default)
    {
        var entry = catalog.GetById(mediaEntityId);
        if (entry is null || (!Path.GetExtension(entry.File.RelativePath).Equals(".avi", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(entry.File.RelativePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))) return null;
        if (entry.Snapshot.ByteLength is null || entry.Snapshot.LastModifiedUtc is null) return null;

        var cacheKeyText = $"{entry.File.RootKey}\n{entry.File.RelativePath}\n{entry.Snapshot.ByteLength}\n{entry.Snapshot.LastModifiedUtc.Value.UtcTicks}";
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKeyText))).ToLowerInvariant();
        Directory.CreateDirectory(cacheDirectory);
        var outputPath = Path.Combine(cacheDirectory, cacheKey + ".mp4");
        if (File.Exists(outputPath)) return TouchCacheFile(outputPath);

        var gate = gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(outputPath)) return TouchCacheFile(outputPath);
            var opened = await sourceAccessor.OpenReadAsync(entry.Media.Source, cancellationToken).ConfigureAwait(false);
            if (opened is not SourceContentOpened source) return null;
            await using var content = source.Content;
            if (content.Length != entry.Snapshot.ByteLength || (content.Facts.LastModifiedUtc is { } modified && modified.ToUniversalTime() != entry.Snapshot.LastModifiedUtc.Value.ToUniversalTime())) return null;

            var partialPath = Path.Combine(cacheDirectory, cacheKey + "." + Guid.NewGuid().ToString("N") + ".partial.mp4");
            try
            {
                var startInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-f", "mp4", partialPath }) startInfo.ArgumentList.Add(argument);
                using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                if (!process.Start()) return null;
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                try
                {
                    await content.Stream.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw;
                }
                _ = await errorTask.ConfigureAwait(false);
                if (process.ExitCode != 0 || !File.Exists(partialPath) || new FileInfo(partialPath).Length == 0) return null;
                File.Move(partialPath, outputPath, overwrite: true);
                PruneCache(outputPath);
                return outputPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch (IOException) { }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void PruneCache(string newestFile)
    {
        try
        {
            var files = new DirectoryInfo(cacheDirectory).EnumerateFiles("*.mp4").OrderByDescending(file => file.LastAccessTimeUtc).ToArray();
            var total = files.Sum(file => file.Length);
            foreach (var old in files.Where(file => !string.Equals(file.FullName, newestFile, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Reverse())
            {
                if (total <= MaximumCacheBytes) break;
                try { total -= old.Length; old.Delete(); } catch (IOException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string TouchCacheFile(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
        return path;
    }
}
