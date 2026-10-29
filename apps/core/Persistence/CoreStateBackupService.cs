using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hispania.Core.Configuration;
using Hispania.ModuleSystem.Contracts;
using Hispania.Runtime;
using Microsoft.Data.Sqlite;

namespace Hispania.Core.Persistence;

internal static class CoreStateBackupService
{
    private const string FormatId = "hispania-core-state-backup";
    private const int FormatVersion = 2;
    private const int CoreSchemaVersion = SqliteCorePersistence.CurrentSchemaVersion;
    private const string DatabaseEntryName = "core.db";
    private const string ManifestEntryName = "manifest.json";
    private const string ModulesDirectoryName = "modules";
    private const long MaximumDatabaseBytes = 1024L * 1024 * 1024;
    private const long MaximumModuleFileBytes = 256L * 1024 * 1024;
    private const long MaximumModuleStoreBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumArchiveBytes = MaximumDatabaseBytes + MaximumModuleStoreBytes + 64L * 1024 * 1024;
    private const int MaximumManifestBytes = 8 * 1024 * 1024;
    private const int MaximumModuleFiles = 20_000;
    private static readonly string[] IncludedState = ["core.db", "modules package inventory and installed packages"];
    private static readonly string[] ExcludedState = ["environment configuration and publisher trust roots", "Data Protection key ring", "module-owned data", "external service databases", "temporary package staging and mutation locks"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private static readonly JsonSerializerOptions ModuleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    internal sealed record BackupResult(long Length, string Sha256);
    private sealed record BackupFile(string Path, long Length, string Sha256);
    private sealed record BackupManifest(
        string Format,
        int FormatVersion,
        string DatabaseFile,
        int CoreSchemaVersion,
        long DatabaseLength,
        string DatabaseSha256,
        BackupFile[]? ModuleFiles,
        DateTimeOffset CreatedUtc,
        string[]? IncludedState,
        string[]? ExcludedState);

    private sealed record StagedState(string DatabasePath, string? ModulesPath, IReadOnlyList<BackupFile> ModuleFiles);

    public static async Task<BackupResult> CreateAsync(CoreStorageOptions options, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var destination = ValidateExternalPath(destinationPath, options, requireExistingFile: false);
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new CoreStateBackupException("Backup destination directory must already exist.");
        StoragePathGuard.EnsureNoReparsePoints(parent);
        if (!Directory.Exists(options.DataDirectory) || !File.Exists(options.DatabasePath))
            throw new CoreStateBackupException("Core database is unavailable for backup.");

        // A complete Core snapshot spans SQLite and the package catalog. Require maintenance
        // mode so no Core writer can change either component while the pair is captured.
        using var coreLease = CoreDataDirectoryLease.Acquire(options.DataDirectory, exclusive: true);
        using var packageLease = AcquireModuleStoreLease(options.DataDirectory, createIfMissing: false);
        RequireRegularFile(options.DatabasePath, "Core database is unavailable for backup.");
        if (File.Exists(destination)) throw new CoreStateBackupException("Backup destination already exists; it will not be overwritten.");

        var workspace = Path.Combine(parent, ".hispania-core-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string? archiveTemporary = null;
        try
        {
            SetPrivateDirectoryPermissions(workspace);
            StoragePathGuard.EnsureNoReparsePoints(workspace);
            var databaseCopy = Path.Combine(workspace, DatabaseEntryName);
            await CreateSqliteSnapshotAsync(options.DatabasePath, databaseCopy, cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(databaseCopy, cancellationToken).ConfigureAwait(false);
            var (databaseLength, databaseSha256) = await GetFileDigestAsync(databaseCopy, MaximumDatabaseBytes, cancellationToken).ConfigureAwait(false);
            SetPrivateFilePermissions(databaseCopy);

            var modulesRoot = Path.Combine(options.DataDirectory, ModulesDirectoryName);
            var moduleFiles = await BuildModuleInventoryAsync(modulesRoot, cancellationToken).ConfigureAwait(false);
            var moduleBytes = moduleFiles.Sum(file => file.Length);
            if (moduleFiles.Count > MaximumModuleFiles || moduleBytes > MaximumModuleStoreBytes)
                throw new CoreStateBackupException("Core module store exceeds the supported backup limits.");

            var manifest = new BackupManifest(FormatId, FormatVersion, DatabaseEntryName, CoreSchemaVersion,
                databaseLength, databaseSha256, moduleFiles.ToArray(), DateTimeOffset.UtcNow, IncludedState, ExcludedState);
            // Keep the incomplete archive inside the private workspace so it never inherits
            // a potentially broader ACL from an arbitrary caller-selected destination folder.
            archiveTemporary = Path.Combine(workspace, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(archiveTemporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                SetPrivateFilePermissions(archiveTemporary);
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                    await using (var stream = manifestEntry.Open())
                        await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);

                    var databaseEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.NoCompression);
                    await using (var target = databaseEntry.Open())
                    await using (var source = new FileStream(databaseCopy, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);

                    foreach (var file in moduleFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourcePath = ResolveModuleFile(modulesRoot, file.Path);
                        var entry = archive.CreateEntry(ModulesDirectoryName + "/" + file.Path, CompressionLevel.NoCompression);
                        await using var target = entry.Open();
                        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await CopyAndVerifyAsync(source, target, file.Length, file.Sha256, MaximumModuleFileBytes, cancellationToken).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            SetPrivateFilePermissions(archiveTemporary);
            var archiveLength = new FileInfo(archiveTemporary).Length;
            if (archiveLength <= 0 || archiveLength > MaximumArchiveBytes)
                throw new CoreStateBackupException("Generated Core backup exceeds the supported size limit.");
            File.Move(archiveTemporary, destination, overwrite: false);
            archiveTemporary = null;
            SetPrivateFilePermissions(destination);
            return new BackupResult(archiveLength, await GetSha256Async(destination, cancellationToken).ConfigureAwait(false));
        }
        catch (CoreStateBackupException) { throw; }
        catch (Exception exception) when (IsExpectedStorageException(exception))
        {
            throw new CoreStateBackupException("Core state backup failed; existing Core data was not modified.", exception);
        }
        finally
        {
            if (archiveTemporary is not null && File.Exists(archiveTemporary)) File.Delete(archiveTemporary);
            DeleteOwnedDirectory(workspace);
        }
    }

    public static async Task RestoreAsync(CoreStorageOptions options, string backupPath, CancellationToken cancellationToken)
        => await RestoreCoreAsync(options, backupPath, afterModulesReplacement: null, afterDatabaseReplacement: null, cancellationToken).ConfigureAwait(false);

    // Internal fault injection is available only to the friend test assembly; no runtime option or request can enable it.
    internal static Task RestoreWithInjectedPostModuleReplacementFailureAsync(CoreStorageOptions options, string backupPath,
        CancellationToken cancellationToken) =>
        RestoreCoreAsync(options, backupPath, static () => throw new IOException("Controlled module-store replacement failure."), null, cancellationToken);

    internal static Task RestoreWithInjectedPostDatabaseReplacementFailureAsync(CoreStorageOptions options, string backupPath,
        CancellationToken cancellationToken) =>
        RestoreCoreAsync(options, backupPath, null, static () => throw new IOException("Controlled database replacement failure."), cancellationToken);

    private static async Task RestoreCoreAsync(CoreStorageOptions options, string backupPath, Action? afterModulesReplacement,
        Action? afterDatabaseReplacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = ValidateExternalPath(backupPath, options, requireExistingFile: true);
        using var coreLease = CoreDataDirectoryLease.Acquire(options.DataDirectory, exclusive: true);
        Directory.CreateDirectory(options.DataDirectory);
        StoragePathGuard.EnsureNoReparsePoints(options.DataDirectory);
        // Preserve an existing Windows DATA_DIRECTORY ACL; archive staging and payload
        // files are secured independently, while Unix keeps the established private mode.
        if (!OperatingSystem.IsWindows()) SetPrivateDirectoryPermissions(options.DataDirectory);

        using var packageLease = AcquireModuleStoreLease(options.DataDirectory, createIfMissing: false);
        var modulesPath = Path.Combine(options.DataDirectory, ModulesDirectoryName);
        var workspace = Path.Combine(options.DataDirectory, ".hispania-core-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string? rollbackDatabase = null;
        string? rollbackModules = null;
        var databaseReplaced = false;
        var modulesChanged = false;
        var preserveRollback = false;
        var hadPreviousDatabase = File.Exists(options.DatabasePath);
        var hadPreviousModules = Directory.Exists(modulesPath);
        try
        {
            SetPrivateDirectoryPermissions(workspace);
            StoragePathGuard.RequireWithin(options.DataDirectory, workspace, "Restore workspace escaped Core data directory.");
            var staged = await ExtractAndValidateAsync(source, workspace, cancellationToken).ConfigureAwait(false);

            if (hadPreviousDatabase)
            {
                RequireRegularFile(options.DatabasePath, "Existing Core database cannot be safely replaced.");
                await NormalizeDatabaseForOfflineReplacementAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
                rollbackDatabase = Path.Combine(options.DataDirectory, ".core-restore-rollback-" + Guid.NewGuid().ToString("N") + ".db");
                await CreateSqliteSnapshotAsync(options.DatabasePath, rollbackDatabase, cancellationToken).ConfigureAwait(false);
                await ValidateDatabaseAsync(rollbackDatabase, cancellationToken).ConfigureAwait(false);
                SetPrivateFilePermissions(rollbackDatabase);
                EnsureNoSqliteSidecars(options.DatabasePath);
            }

            // The Core-wide exclusive maintenance lease prevents normal host activity. Release
            // the package lock only immediately before the offline directory rename.
            packageLease?.Dispose();
            if (hadPreviousModules)
            {
                StoragePathGuard.EnsureTreeHasNoReparsePoints(modulesPath);
                rollbackModules = Path.Combine(options.DataDirectory, ".core-restore-rollback-" + Guid.NewGuid().ToString("N") + ".modules");
                Directory.Move(modulesPath, rollbackModules);
                modulesChanged = true;
            }
            if (staged.ModulesPath is not null)
            {
                Directory.Move(staged.ModulesPath, modulesPath);
                modulesChanged = true;
            }
            afterModulesReplacement?.Invoke();

            File.Move(staged.DatabasePath, options.DatabasePath, overwrite: hadPreviousDatabase);
            databaseReplaced = true;
            afterDatabaseReplacement?.Invoke();
            SetPrivateFilePermissions(options.DatabasePath);
            await ValidateDatabaseAsync(options.DatabasePath, cancellationToken).ConfigureAwait(false);
            await ValidateRestoredModuleStoreAsync(modulesPath, cancellationToken).ConfigureAwait(false);

            if (rollbackDatabase is not null)
            {
                File.Delete(rollbackDatabase);
                rollbackDatabase = null;
            }
            if (rollbackModules is not null && Directory.Exists(rollbackModules))
            {
                DeleteOwnedDirectory(rollbackModules);
                rollbackModules = null;
            }
        }
        catch (Exception exception)
        {
            try
            {
                await RollbackIfNeededAsync(options.DatabasePath, rollbackDatabase, databaseReplaced, hadPreviousDatabase,
                    modulesPath, rollbackModules, modulesChanged, hadPreviousModules, CancellationToken.None).ConfigureAwait(false);
                if (databaseReplaced && hadPreviousDatabase) rollbackDatabase = null;
                if (modulesChanged && hadPreviousModules) rollbackModules = null;
            }
            catch (Exception rollbackException)
            {
                preserveRollback = true;
                throw new CoreStateBackupException("Core restore failed and rollback could not complete; rollback material was preserved for recovery.", rollbackException);
            }
            if (exception is CoreStateBackupException backupException) throw backupException;
            if (exception is OperationCanceledException) throw;
            throw new CoreStateBackupException("Core state restore failed; the previous database and module store were preserved.", exception);
        }
        finally
        {
            if (!preserveRollback && rollbackDatabase is not null && File.Exists(rollbackDatabase)) File.Delete(rollbackDatabase);
            if (!preserveRollback && rollbackModules is not null && Directory.Exists(rollbackModules)) DeleteOwnedDirectory(rollbackModules);
            DeleteOwnedDirectory(workspace);
        }
    }

    private static async Task<StagedState> ExtractAndValidateAsync(string source, string workspace, CancellationToken cancellationToken)
    {
        RequireRegularFile(source, "Core backup file is not a regular file.");
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length is <= 0 or > MaximumArchiveBytes)
            throw new CoreStateBackupException("Core backup file size is outside the supported limit.");

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count < 2 || archive.Entries.Count > MaximumModuleFiles + 2)
            throw new CoreStateBackupException("Core backup has an unexpected entry count.");
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntryName(entry.FullName);
            if (!entryNames.Add(entry.FullName)) throw new CoreStateBackupException("Core backup contains duplicate or case-colliding entries.");
            if (IsZipSymbolicLink(entry)) throw new CoreStateBackupException("Core backup cannot contain symbolic links or reparse points.");
        }
        var entries = archive.Entries.ToDictionary(entry => entry.FullName, StringComparer.Ordinal);
        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry) || !entries.TryGetValue(DatabaseEntryName, out var databaseEntry))
            throw new CoreStateBackupException("Core backup entries do not match the supported format.");
        if (manifestEntry.Length is <= 0 or > MaximumManifestBytes || databaseEntry.Length is <= 0 or > MaximumDatabaseBytes)
            throw new CoreStateBackupException("Core backup entry size is outside the supported limit.");

        BackupManifest? manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            using var bounded = new MemoryStream();
            await CopyBoundedAsync(manifestStream, bounded, MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bounded.ToArray());
            RejectDuplicateJsonProperties(document.RootElement);
            manifest = document.RootElement.Deserialize<BackupManifest>(JsonOptions);
        }
        var moduleFiles = manifest?.ModuleFiles;
        if (manifest is null || manifest.Format != FormatId || manifest.FormatVersion != FormatVersion ||
            manifest.DatabaseFile != DatabaseEntryName || manifest.CoreSchemaVersion != CoreSchemaVersion ||
            manifest.IncludedState is null || !manifest.IncludedState.SequenceEqual(IncludedState, StringComparer.Ordinal) ||
            manifest.ExcludedState is null || !manifest.ExcludedState.SequenceEqual(ExcludedState, StringComparer.Ordinal) ||
            manifest.DatabaseLength != databaseEntry.Length || manifest.DatabaseLength <= 0 ||
            manifest.DatabaseSha256 is null || !IsSha256(manifest.DatabaseSha256) || moduleFiles is null || moduleFiles.Length > MaximumModuleFiles)
            throw new CoreStateBackupException("Core backup manifest is invalid or incompatible.");

        var moduleLookup = new Dictionary<string, BackupFile>(StringComparer.OrdinalIgnoreCase);
        long moduleBytes = 0;
        foreach (var file in moduleFiles)
        {
            if (file is null || file.Path is null || file.Sha256 is null)
                throw new CoreStateBackupException("Core backup module inventory contains a null entry.");
            ValidateModuleRelativePath(file.Path);
            if (file.Length < 0 || file.Length > MaximumModuleFileBytes || !IsSha256(file.Sha256) || !moduleLookup.TryAdd(file.Path, file))
                throw new CoreStateBackupException("Core backup module inventory is invalid or exceeds its limits.");
            moduleBytes = checked(moduleBytes + file.Length);
        }
        if (moduleBytes > MaximumModuleStoreBytes) throw new CoreStateBackupException("Core backup module store exceeds the supported size limit.");

        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            ManifestEntryName,
            DatabaseEntryName
        };
        foreach (var file in moduleFiles) expectedNames.Add(ModulesDirectoryName + "/" + file.Path);
        if (expectedNames.Count != archive.Entries.Count || archive.Entries.Any(entry => !expectedNames.Contains(entry.FullName)))
            throw new CoreStateBackupException("Core backup contains missing or unlisted entries.");

        var stagedDatabase = Path.Combine(workspace, DatabaseEntryName);
        await using (var databaseStream = databaseEntry.Open())
        await using (var output = new FileStream(stagedDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            SetPrivateFilePermissions(stagedDatabase);
            await CopyBoundedAsync(databaseStream, output, MaximumDatabaseBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        var (databaseLength, databaseSha256) = await GetFileDigestAsync(stagedDatabase, MaximumDatabaseBytes, cancellationToken).ConfigureAwait(false);
        if (databaseLength != manifest.DatabaseLength || !FixedTimeHashEquals(databaseSha256, manifest.DatabaseSha256))
            throw new CoreStateBackupException("Core backup database integrity verification failed.");
        await ValidateDatabaseAsync(stagedDatabase, cancellationToken).ConfigureAwait(false);

        string? stagedModules = null;
        if (moduleFiles.Length != 0)
        {
            stagedModules = Path.Combine(workspace, ModulesDirectoryName);
            Directory.CreateDirectory(stagedModules);
            SetPrivateDirectoryPermissions(stagedModules);
            foreach (var file in moduleFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryName = ModulesDirectoryName + "/" + file.Path;
                var entry = entries[entryName];
                if (entry.Length != file.Length) throw new CoreStateBackupException("Core backup module file length is inconsistent.");
                var outputPath = ResolveModuleFile(stagedModules, file.Path);
                var outputDirectory = Path.GetDirectoryName(outputPath)!;
                Directory.CreateDirectory(outputDirectory);
                StoragePathGuard.EnsureNoReparsePoints(stagedModules);
                StoragePathGuard.RequireWithin(stagedModules, outputPath, "Core backup module path escaped staging.");
                await using var moduleInput = entry.Open();
                await using var moduleOutput = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                SetPrivateFilePermissions(outputPath);
                await CopyAndVerifyAsync(moduleInput, moduleOutput, file.Length, file.Sha256, MaximumModuleFileBytes, cancellationToken).ConfigureAwait(false);
                await moduleOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
                moduleOutput.Flush(flushToDisk: true);
            }
            await ValidateRestoredModuleStoreAsync(stagedModules, cancellationToken).ConfigureAwait(false);
            var actualInventory = await BuildModuleInventoryAsync(stagedModules, cancellationToken).ConfigureAwait(false);
            if (!InventoriesEqual(moduleFiles, actualInventory))
                throw new CoreStateBackupException("Staged Core module store does not match its manifest inventory.");
        }
        return new StagedState(stagedDatabase, stagedModules, moduleFiles);
    }

    private static async Task<IReadOnlyList<BackupFile>> BuildModuleInventoryAsync(string modulesRoot, CancellationToken cancellationToken)
    {
        var files = new List<BackupFile>();
        if (!Directory.Exists(modulesRoot)) return files;
        StoragePathGuard.EnsureNoReparsePoints(modulesRoot);
        StoragePathGuard.EnsureTreeHasNoReparsePoints(modulesRoot);

        var stateRoot = Path.Combine(modulesRoot, ".package-state");
        if (Directory.Exists(stateRoot)) ModuleExtensionCatalog.ValidateBackupPackageSet(modulesRoot);

        foreach (var top in Directory.EnumerateFileSystemEntries(modulesRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(top);
            if (name == ".package-state")
            {
                await AddPackageStateFilesAsync(modulesRoot, top, files, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (Directory.Exists(top) && IsOwnedInstallerStagingDirectory(name))
            {
                StoragePathGuard.EnsureTreeHasNoReparsePoints(top);
                continue;
            }
            if (!Directory.Exists(top) || !IsSafeModuleId(name))
                throw new CoreStateBackupException("Core module store contains an unrecognized top-level item.");
            foreach (var versionDirectory in Directory.EnumerateDirectories(top).Order(StringComparer.Ordinal))
            {
                var versionName = Path.GetFileName(versionDirectory);
                StoragePathGuard.EnsureTreeHasNoReparsePoints(versionDirectory);
                await AddInstalledPackageFilesAsync(modulesRoot, name, versionName, versionDirectory, files, cancellationToken).ConfigureAwait(false);
            }
            if (Directory.EnumerateFiles(top).Any()) throw new CoreStateBackupException("Module inventory contains files outside an installed package directory.");
        }

        if (files.Count > MaximumModuleFiles || files.Sum(file => file.Length) > MaximumModuleStoreBytes)
            throw new CoreStateBackupException("Core module store exceeds the supported backup limits.");
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    private static async Task AddInstalledPackageFilesAsync(string modulesRoot, string moduleId, string versionName,
        string packageDirectory, List<BackupFile> output, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageDirectory, "module.json");
        RequireRegularFile(manifestPath, "Installed module package is missing a regular manifest.");
        var manifestInfo = new FileInfo(manifestPath);
        if (manifestInfo.Length is <= 0 or > 1024 * 1024) throw new CoreStateBackupException("Installed module manifest exceeds the supported size limit.");
        await using var manifestInput = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using var manifestDocument = await JsonDocument.ParseAsync(manifestInput, cancellationToken: cancellationToken).ConfigureAwait(false);
        RejectDuplicateJsonProperties(manifestDocument.RootElement);
        var manifest = manifestDocument.RootElement.Deserialize<ModuleManifest>(ModuleJsonOptions)
            ?? throw new CoreStateBackupException("Installed module manifest is invalid.");
        PackageDependencyResolver.ValidateManifest(manifest);
        if (!string.Equals(manifest.Id, moduleId, StringComparison.Ordinal) ||
            !string.Equals(manifest.PackageVersion ?? manifest.Version, versionName, StringComparison.Ordinal))
            throw new CoreStateBackupException("Installed module directory identity disagrees with its manifest.");
        ModuleExtensionCatalog.ValidateBackupPackage(manifest, packageDirectory);

        var expected = new HashSet<string>(manifest.Files.Keys.Select(path => path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
        {
            "module.json"
        };
        var actual = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(packageDirectory, path).Replace('\\', '/')).ToArray();
        if (actual.Length != expected.Count || actual.Any(path => !expected.Remove(path)))
            throw new CoreStateBackupException("Installed module package contains unowned files.");

        foreach (var relative in actual.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(packageDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var storeRelative = Path.GetRelativePath(modulesRoot, path).Replace('\\', '/');
            ValidateModuleRelativePath(storeRelative);
            output.Add(await CreateBackupFileAsync(path, storeRelative, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task AddPackageStateFilesAsync(string modulesRoot, string stateRoot, List<BackupFile> output,
        CancellationToken cancellationToken)
    {
        StoragePathGuard.EnsureTreeHasNoReparsePoints(stateRoot);
        foreach (var path in Directory.EnumerateFileSystemEntries(stateRoot).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name == "mutation.lock")
            {
                if (Directory.Exists(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new CoreStateBackupException("Package mutation lock is not a regular file.");
                continue;
            }
            if (name == "staging" && Directory.Exists(path))
            {
                StoragePathGuard.EnsureTreeHasNoReparsePoints(path);
                continue;
            }
            if (Directory.Exists(path) && name == "artifacts")
            {
                StoragePathGuard.EnsureTreeHasNoReparsePoints(path);
                foreach (var artifact in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
                {
                    if (Directory.Exists(artifact)) throw new CoreStateBackupException("Package artifact store contains an unexpected directory.");
                    var filename = Path.GetFileName(artifact);
                    var digest = Path.GetFileNameWithoutExtension(filename);
                    if (!filename.EndsWith(".hispania", StringComparison.Ordinal) || !IsSha256(digest))
                        throw new CoreStateBackupException("Package artifact store contains an invalid artifact name.");
                    var candidate = await PackageArchiveInspector.InspectAsync(artifact, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(candidate.ArtifactHash, digest, StringComparison.OrdinalIgnoreCase))
                        throw new CoreStateBackupException("Retained package artifact hash disagrees with its name.");
                    output.Add(await CreateBackupFileAsync(artifact,
                        Path.GetRelativePath(modulesRoot, artifact).Replace('\\', '/'), cancellationToken).ConfigureAwait(false));
                }
                continue;
            }
            if (File.Exists(path) && name is "catalog.json" or "previous.json" or "journal.json")
            {
                output.Add(await CreateBackupFileAsync(path,
                    Path.GetRelativePath(modulesRoot, path).Replace('\\', '/'), cancellationToken).ConfigureAwait(false));
                continue;
            }
            throw new CoreStateBackupException("Package state contains an unrecognized item.");
        }
    }

    private static async Task<BackupFile> CreateBackupFileAsync(string path, string relativePath, CancellationToken cancellationToken)
    {
        ValidateModuleRelativePath(relativePath);
        RequireRegularFile(path, "Core module store contains a non-regular file.");
        var (length, sha256) = await GetFileDigestAsync(path, MaximumModuleFileBytes, cancellationToken).ConfigureAwait(false);
        return new BackupFile(relativePath, length, sha256);
    }

    private static async Task ValidateRestoredModuleStoreAsync(string modulesRoot, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modulesRoot)) return;
        var inventory = await BuildModuleInventoryAsync(modulesRoot, cancellationToken).ConfigureAwait(false);
        if (inventory.Count > MaximumModuleFiles || inventory.Sum(file => file.Length) > MaximumModuleStoreBytes)
            throw new CoreStateBackupException("Restored Core module store exceeds the supported limits.");
    }

    private static FileStream? AcquireModuleStoreLease(string dataDirectory, bool createIfMissing)
    {
        var modulesRoot = Path.Combine(dataDirectory, ModulesDirectoryName);
        if (!Directory.Exists(modulesRoot) && !createIfMissing) return null;
        StoragePathGuard.EnsureNoReparsePoints(modulesRoot);
        Directory.CreateDirectory(modulesRoot);
        StoragePathGuard.EnsureNoReparsePoints(modulesRoot);
        StoragePathGuard.EnsureTreeHasNoReparsePoints(modulesRoot);
        var stateRoot = Path.Combine(modulesRoot, ".package-state");
        Directory.CreateDirectory(stateRoot);
        StoragePathGuard.RequireWithin(modulesRoot, stateRoot, "Package state escaped the module store.");
        StoragePathGuard.EnsureNoReparsePoints(stateRoot);
        var lockPath = Path.Combine(stateRoot, "mutation.lock");
        StoragePathGuard.EnsureNoReparsePoints(lockPath);
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return stream;
        }
        catch (IOException exception)
        {
            throw new CoreStateBackupException("Core module inventory is changing; retry backup or restore after the package operation completes.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new CoreStateBackupException("Core module store is not accessible for backup or restore.", exception);
        }
    }

    private static async Task CreateSqliteSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await using (source.ConfigureAwait(false))
        {
            await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 10
            }.ToString());
            await using (destination.ConfigureAwait(false))
            {
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }
        }
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        RequireRegularFile(path, "Core database file is not regular.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var integrity = await ScalarStringAsync(connection, "PRAGMA integrity_check;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new CoreStateBackupException("Core database failed SQLite integrity verification.");
            var schema = await ScalarStringAsync(connection,
                "SELECT CASE WHEN COUNT(*) = 3 AND MIN(version) = 1 AND MAX(version) = $version THEN 'ok' ELSE 'invalid' END FROM core_schema_migrations;",
                cancellationToken, ("$version", CoreSchemaVersion)).ConfigureAwait(false);
            if (!string.Equals(schema, "ok", StringComparison.Ordinal))
                throw new CoreStateBackupException("Core database schema version is incompatible.");
            foreach (var table in new[] { "core_instance", "core_identities", "core_credentials", "core_roles", "core_permissions", "core_role_permissions", "core_subject_roles", "core_sessions", "core_bootstrap_state", "core_security_audit" })
            {
                var exists = await ScalarStringAsync(connection,
                    "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);", cancellationToken,
                    ("$name", table)).ConfigureAwait(false);
                if (!string.Equals(exists, "1", StringComparison.Ordinal))
                    throw new CoreStateBackupException("Core database is missing a required state table.");
            }
            var instanceCount = await ScalarStringAsync(connection, "SELECT COUNT(*) FROM core_instance WHERE singleton_id = 1;", cancellationToken).ConfigureAwait(false);
            var bootstrapCount = await ScalarStringAsync(connection, "SELECT COUNT(*) FROM core_bootstrap_state WHERE singleton_id = 1;", cancellationToken).ConfigureAwait(false);
            if (instanceCount != "1" || bootstrapCount != "1")
                throw new CoreStateBackupException("Core database contains incomplete singleton state.");
        }
    }

    private static async Task NormalizeDatabaseForOfflineReplacementAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var checkpoint = await ScalarStringAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(checkpoint, "0", StringComparison.Ordinal))
                throw new CoreStateBackupException("Core database could not checkpoint before offline restore.");
            var journal = await ScalarStringAsync(connection, "PRAGMA journal_mode=DELETE;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals(journal, "delete", StringComparison.OrdinalIgnoreCase))
                throw new CoreStateBackupException("Core database could not leave WAL mode for offline restore.");
        }
    }

    private static async Task RollbackIfNeededAsync(string databasePath, string? rollbackDatabase, bool databaseReplaced,
        bool hadPreviousDatabase, string modulesPath, string? rollbackModules, bool modulesChanged, bool hadPreviousModules,
        CancellationToken cancellationToken)
    {
        if (modulesChanged)
        {
            if (Directory.Exists(modulesPath)) DeleteOwnedDirectory(modulesPath);
            if (hadPreviousModules && rollbackModules is not null && Directory.Exists(rollbackModules))
                Directory.Move(rollbackModules, modulesPath);
            else if (hadPreviousModules)
                throw new IOException("Module-store rollback material is missing.");
        }
        // NormalizeDatabaseForOfflineReplacementAsync can change SQLite journal mode before
        // the new database is moved into place. Restore the captured pre-operation snapshot
        // on every failed restore, not only when the replacement move completed.
        if (rollbackDatabase is not null && File.Exists(rollbackDatabase))
        {
            if (hadPreviousDatabase)
                File.Move(rollbackDatabase, databasePath, overwrite: true);
            else if (!hadPreviousDatabase && File.Exists(databasePath))
                File.Delete(databasePath);
            else if (hadPreviousDatabase)
                throw new IOException("Database rollback material is missing.");
        }
        else if (databaseReplaced && hadPreviousDatabase)
        {
            throw new IOException("Database rollback material is missing.");
        }
        else if (databaseReplaced && !hadPreviousDatabase && File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
        if (File.Exists(databasePath)) await ValidateDatabaseAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await ValidateRestoredModuleStoreAsync(modulesPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long Length, string Sha256)> GetFileDigestAsync(string path, long maximumLength, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length < 0 || info.Length > maximumLength) throw new CoreStateBackupException("Core state file size is outside the supported limit.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return (info.Length, Convert.ToHexString(hash));
    }

    private static async Task<string> GetSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task CopyAndVerifyAsync(Stream source, Stream destination, long expectedLength, string expectedHash,
        long maximumLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximumLength || total > expectedLength) throw new CoreStateBackupException("Core state file exceeds its declared size.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (total != expectedLength || !FixedTimeHashEquals(actual, expectedHash))
            throw new CoreStateBackupException("Core state file integrity verification failed.");
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes) throw new CoreStateBackupException("Core backup content exceeds the supported size limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ValidateExternalPath(string path, CoreStorageOptions options, bool requireExistingFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new CoreStateBackupException("Backup paths must be absolute.");
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            StoragePathGuard.EnsureNoReparsePoints(fullPath);
            StoragePathGuard.RequireOutside(fullPath, options.DataDirectory, "Backup files must remain outside DATA_DIRECTORY.");
            StoragePathGuard.RequireOutside(fullPath, AppContext.BaseDirectory, "Backup files must remain outside the application directory.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new CoreStateBackupException("Backup path is invalid or outside the permitted storage boundary.", exception);
        }
        if (requireExistingFile) RequireRegularFile(fullPath, "Core backup file is unavailable.");
        return fullPath;
    }

    private static string ResolveModuleFile(string modulesRoot, string relativePath)
    {
        ValidateModuleRelativePath(relativePath);
        var root = Path.GetFullPath(modulesRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(modulesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CoreStateBackupException("Core module path escaped its authorized store.");
        return full;
    }

    private static void ValidateModuleRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Contains(':') ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new CoreStateBackupException("Core module inventory contains an unsafe relative path.");
    }

    private static void ValidateArchiveEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.StartsWith('/') || name.Contains(':') ||
            name.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new CoreStateBackupException("Core backup contains an unsafe archive path.");
        if (name is ManifestEntryName or DatabaseEntryName) return;
        const string prefix = "modules/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new CoreStateBackupException("Core backup contains an unexpected root entry.");
        ValidateModuleRelativePath(name[prefix.Length..]);
    }

    private static bool IsZipSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsOwnedInstallerStagingDirectory(string name) =>
        name.StartsWith(".staging-", StringComparison.Ordinal) && Guid.TryParseExact(name[9..], "N", out _);

    private static bool IsSafeModuleId(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool FixedTimeHashEquals(string left, string right) => IsSha256(left) && IsSha256(right) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static bool InventoriesEqual(IReadOnlyList<BackupFile> expected, IReadOnlyList<BackupFile> actual) =>
        expected.Count == actual.Count && expected.OrderBy(item => item.Path, StringComparer.Ordinal)
            .Zip(actual.OrderBy(item => item.Path, StringComparer.Ordinal))
            .All(pair => string.Equals(pair.First.Path, pair.Second.Path, StringComparison.Ordinal) &&
                pair.First.Length == pair.Second.Length && FixedTimeHashEquals(pair.First.Sha256, pair.Second.Sha256));

    private static void RejectDuplicateJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new CoreStateBackupException("Core backup JSON contains duplicate properties.");
                RejectDuplicateJsonProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) RejectDuplicateJsonProperties(item);
        }
    }

    private static void RequireRegularFile(string path, string errorMessage)
    {
        StoragePathGuard.EnsureNoReparsePoints(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CoreStateBackupException(errorMessage);
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken,
        (string Name, object Value)? parameter = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameter is { } value) command.Parameters.AddWithValue(value.Name, value.Value);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsExpectedStorageException(Exception exception) => exception is IOException or UnauthorizedAccessException or
        SqliteException or JsonException or InvalidDataException or ArgumentException or NotSupportedException;

    private static void EnsureNoSqliteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = databasePath + suffix;
            StoragePathGuard.EnsureNoReparsePoints(sidecar);
            if (File.Exists(sidecar)) throw new CoreStateBackupException("SQLite sidecar files remain after the offline checkpoint.");
        }
    }

    private static void SetPrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            var security = new FileSecurity();
            ApplyPrivateWindowsAcl(security);
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
        }
        else if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetPrivateDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows() && Directory.Exists(path))
        {
            var security = new DirectorySecurity();
            ApplyPrivateWindowsAcl(security);
            FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(path), security);
        }
        else if (!OperatingSystem.IsWindows() && Directory.Exists(path))
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyPrivateWindowsAcl(ObjectSecurity security)
    {
        var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Current Windows identity has no security identifier.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        var rights = FileSystemRights.FullControl;
        if (security is DirectorySecurity)
        {
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            ((DirectorySecurity)security).AddAccessRule(new FileSystemAccessRule(user, rights, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
            ((DirectorySecurity)security).AddAccessRule(new FileSystemAccessRule(system, rights, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }
        else
        {
            ((FileSecurity)security).AddAccessRule(new FileSystemAccessRule(user, rights, AccessControlType.Allow));
            ((FileSecurity)security).AddAccessRule(new FileSystemAccessRule(system, rights, AccessControlType.Allow));
        }
    }

    private static void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        StoragePathGuard.EnsureTreeHasNoReparsePoints(path);
        Directory.Delete(path, recursive: true);
    }
}
