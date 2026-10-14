using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hispania.Hosting;
using Hispania.Identity.Application;
using Microsoft.Data.Sqlite;
using Hispania.Platform.Persistence.Sqlite;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase18ProductionDeployment")]
public sealed class ArtifactProcessTests
{
    [Fact]
    public void PublishedArtifactHasCompleteManifestAndRuntimeOnlyDependencies()
    {
        var manifest = ArtifactManifest.Verify(Artifact);
        Assert.Equal(OperatingSystem.IsWindows() ? "win-x64" : "linux-x64", manifest.TargetRid);
        Assert.Equal(40, manifest.GitSha.Length);
        Assert.Contains("10.0", manifest.RuntimePrerequisite, StringComparison.Ordinal);
        Assert.Contains(manifest.Files.Keys, path => path.EndsWith(".deps.json", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Files.Keys, path => path.Split('/').Any(part => part is ".git" or "node_modules" or "demo-assets"));
        Assert.DoesNotContain(manifest.Files.Keys, path => path.EndsWith(".csproj", StringComparison.Ordinal) || path.EndsWith(".sln", StringComparison.Ordinal));
        Assert.DoesNotContain(manifest.Files.Keys, path => path.Contains("ArchitectureTest", StringComparison.Ordinal) || path.EndsWith(".Tests.dll", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArtifactOnlyCleanHostStartsFromUnrelatedCwdAndServesBothWebBuildsAfterRestart()
    {
        using var fixture = new HostFixture();
        foreach (var build in new[] { "modern", "classic" })
        {
            fixture.WriteConfiguration(new { webBuild = build });
            using var server = fixture.Start(); await fixture.ReadyAsync(server);
            using var response = await fixture.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync(); Assert.Contains("/assets/", html, StringComparison.Ordinal);
            var asset = System.Text.RegularExpressions.Regex.Match(html, "src=\"([^\"]+\\.js)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant).Groups[1].Value;
            using var script = await fixture.Client.GetAsync(asset); Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Contains("javascript", script.Content.Headers.ContentType!.MediaType!, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/authorization/users")).StatusCode);
            Assert.False((await fixture.Client.PostAsJsonAsync("/_test/shutdown", new { })).IsSuccessStatusCode);
            Assert.Throws<DeploymentException>(() => InstallationOperations.Install(Artifact, fixture.Home, "server"));
            await fixture.StopAsync(server);
        }
        Assert.True(File.Exists(Path.Combine(fixture.Home, "state", "platform", "identity.sqlite")));
    }

    [Fact]
    public async Task BootstrapThenBackupRestoreReprovisionsSecretsWithoutRepeatingConsumedBootstrap()
    {
        using var fixture = new HostFixture();
        var token = Guid.NewGuid().ToString("N"); var password = "Unique!" + Guid.NewGuid().ToString("N");
        fixture.WriteConfiguration(new { bootstrapSecret = "env:BOOTSTRAP", bootstrapPasswordSecret = "env:ADMIN_PASSWORD", bootstrapUsername = "deployment-operator" });
        var result = await fixture.CommandAsync(["--home", fixture.Home, "--bootstrap"], new() { ["HISPANIA_SECRET_BOOTSTRAP"] = token, ["HISPANIA_SECRET_ADMIN_PASSWORD"] = password });
        Assert.Equal(0, result.Code); Assert.DoesNotContain(token, result.Output, StringComparison.Ordinal); Assert.DoesNotContain(password, result.Output, StringComparison.Ordinal);
        fixture.WriteConfiguration(new { }); // Consumed bootstrap material is no longer needed.
        using (var server = fixture.Start()) { await fixture.ReadyAsync(server); await AssertLoginAsync(fixture.Client, password); await fixture.StopAsync(server); }
        var bundle = Path.Combine(fixture.Home, "backups", "disaster");
        Assert.Equal(0, (await fixture.CommandAsync(["--backup-create", Path.Combine(fixture.Home, "state"), bundle])).Code);
        Assert.Equal(0, (await fixture.CommandAsync(["--backup-verify", bundle])).Code);
        foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.False(Contains(bytes, System.Text.Encoding.UTF8.GetBytes(token)));
            Assert.False(Contains(bytes, System.Text.Encoding.UTF8.GetBytes(password)));
            Assert.DoesNotContain(".pfx", file, StringComparison.Ordinal);
        }
        using var recovered = new HostFixture();
        Directory.Delete(Path.Combine(recovered.Home, "state"), true);
        Assert.Equal(0, (await fixture.CommandAsync(["--backup-restore", bundle, Path.Combine(recovered.Home, "state")])).Code);
        using var restoredServer = recovered.Start(); await recovered.ReadyAsync(restoredServer); await AssertLoginAsync(recovered.Client, password); await recovered.StopAsync(restoredServer);
        var identity = Path.Combine(recovered.Home, "state", "platform", "identity.sqlite");
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = identity, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT consumed FROM authorization_bootstrap WHERE id=1";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ServerRejectsMissingSecretAndOccupiedPortWithoutLeakingOrChangingPort()
    {
        using var fixture = new HostFixture();
        fixture.WriteConfiguration(new { bootstrapSecret = "env:MISSING_PHASE18" });
        var missing = await fixture.CommandAsync(["--home", fixture.Home, "--check"]);
        Assert.Equal(3, missing.Code); Assert.Contains("Missing secret", missing.Output, StringComparison.Ordinal);
        fixture.WriteConfiguration(new { });
        using var blocker = new TcpListener(IPAddress.Loopback, fixture.Port); blocker.Start();
        var occupied = await fixture.CommandAsync(["--home", fixture.Home]);
        Assert.Equal(4, occupied.Code); Assert.Contains("endpoint availability", occupied.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OfflineUpgradePreservesVerifiedBackupAndRestoreRollbackRejectsFutureMigratedData()
    {
        var predecessor = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_PREDECESSOR") ?? throw new InvalidOperationException("Mandatory archived Phase 17 artifact missing.");
        var expectedPredecessorSha = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_PREDECESSOR_SHA") ?? "c3a827a4b828cff37785884e889689af7cc5fce2";
        Assert.Equal(expectedPredecessorSha, ArtifactManifest.Verify(predecessor).GitSha);
        using var fixture = new HostFixture(predecessor);
        var password = "PHASE18_SUPER_SECRET_CANARY_UPGRADE_" + Guid.NewGuid().ToString("N");
        var legacyArgs = new[] { "--home", fixture.Home };
        Directory.CreateDirectory(Path.Combine(fixture.Home, "library-source"));
        fixture.WriteConfiguration(new { bootstrapSecret = "env:PHASE18_BOOTSTRAP", bootstrapPasswordSecret = "env:PHASE18_PASSWORD", bootstrapUsername = "deployment-operator" });
        using (var initialize = fixture.Start(legacyArgs)) { await fixture.ReadyAsync(initialize); await fixture.StopAsync(initialize); }
        var identityPath = Path.Combine(fixture.Home, "state", "platform", "identity.sqlite");
        var identityService = new IdentityService(new SqliteIdentityStore(identityPath));
        var created = await identityService.CreateUserAsync("deployment-operator", "Deployment Operator", password);
        Assert.True(created.Succeeded);
        var bootstrapToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var bootstrapEnvironment = new Dictionary<string, string> { ["Identity__Bootstrap__Token"] = bootstrapToken };
        using (var bootstrapHost = fixture.Start(legacyArgs, bootstrapEnvironment))
        {
            await fixture.ReadyAsync(bootstrapHost);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/authorization/bootstrap-admin")
            {
                Content = JsonContent.Create(new { userId = created.Value!.Id.Value })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bootstrap", bootstrapToken);
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await AssertLoginAsync(fixture.Client, password);
            await fixture.StopAsync(bootstrapHost);
        }
        fixture.WriteConfiguration(new { });
        using (var server = fixture.Start(legacyArgs))
        {
            await fixture.ReadyAsync(server);
            await AssertLoginAsync(fixture.Client, password);
            await Assert.ThrowsAsync<DeploymentException>(() => InstallationOperations.UpgradeAsync(Artifact, fixture.Home));
            await fixture.StopAsync(server);
        }
        fixture.WriteConfiguration(new { webBuild = "classic", logLevel = "Warning" });
        var before = File.ReadAllBytes(Path.Combine(fixture.Home, "config", "production.json"));
        var backups = Path.Combine(fixture.Home, "backups");
        var preservedBackups = backups + ".preserved";
        Directory.Move(backups, preservedBackups); File.WriteAllText(backups, "fault: backup destination unavailable");
        try
        {
            await Assert.ThrowsAsync<DeploymentException>(() => InstallationOperations.UpgradeAsync(Artifact, fixture.Home));
            Assert.Equal(ArtifactManifest.Verify(predecessor).GitSha, ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).GitSha);
        }
        finally { File.Delete(backups); Directory.Move(preservedBackups, backups); }
        InstallationOperations.RecoverBeforeStartup(fixture.Home);
        var bundle = await InstallationOperations.UpgradeAsync(Artifact, fixture.Home);
        Assert.True(File.Exists(Path.Combine(bundle, "manifest.json")));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Home, "config", "production.json")));
        Assert.NotEqual(ArtifactManifest.Verify(predecessor).Files["server/Hispania.Server.dll"], ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).Files["server/Hispania.Server.dll"]);
        fixture.WriteConfiguration(new { ffprobe = "missing-phase18-dependency" });
        Assert.Equal(5, (await fixture.CommandAsync(["--home", fixture.Home])).Code);
        InstallationOperations.RecoverBeforeStartup(fixture.Home);
        Assert.Equal(ArtifactManifest.Verify(predecessor).GitSha, ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).GitSha);
        Assert.True(Directory.Exists(bundle));
        fixture.WriteConfiguration(new { });
        using (var binaryRollback = fixture.Start(legacyArgs)) { await fixture.ReadyAsync(binaryRollback); await AssertLoginAsync(fixture.Client, password); await fixture.StopAsync(binaryRollback); }
        bundle = await InstallationOperations.UpgradeAsync(Artifact, fixture.Home);
        using (var upgraded = fixture.Start()) { await fixture.ReadyAsync(upgraded); await AssertLoginAsync(fixture.Client, password); await fixture.StopAsync(upgraded); }
        Assert.Throws<DeploymentException>(() => InstallationOperations.RecoverBeforeStartup(fixture.Home));
        var identity = Path.Combine(fixture.Home, "state", "platform", "identity.sqlite");
        // Controlled owner-history fixture: new data now exceeds old readers. No invented release.
        using (var database = new SqliteConnection($"Data Source={identity}"))
        {
            database.Open(); using var command = database.CreateCommand();
            command.CommandText = "UPDATE hispania_schema_history SET version=999"; command.ExecuteNonQuery();
        }
        var incompatible = await fixture.CommandAsync(["--home", fixture.Home]); Assert.Equal(4, incompatible.Code);
        SqliteConnection.ClearAllPools();
        await InstallationOperations.RestoreRollbackAsync(fixture.Home, bundle);
        Assert.Equal(ArtifactManifest.Verify(predecessor).GitSha, ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).GitSha);
        using var rolledBack = fixture.Start(legacyArgs); await fixture.ReadyAsync(rolledBack); await AssertLoginAsync(fixture.Client, password); await fixture.StopAsync(rolledBack);
        Assert.True(Directory.Exists(bundle));
    }

    [Fact]
    public async Task OfficialCompleteRc1MigratesToPlatformOnlyAndCanRestoreTheCompleteFixture()
    {
        var rc1 = Environment.GetEnvironmentVariable("HISPANIA_PHASE21V_RC1_ARTIFACT") ??
            throw new InvalidOperationException("The official v0.7.0-rc.1 Complete publication is required for this migration gate.");
        var platform = Environment.GetEnvironmentVariable("HISPANIA_PHASE21V_PLATFORM_ARTIFACT") ??
            throw new InvalidOperationException("A clean Platform-only publication is required for this migration gate.");
        var externalRoot = Path.Combine(Path.GetTempPath(), "hispania-phase21v-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(externalRoot);
        var externalFile = Path.Combine(externalRoot, "retained-media-fixture.bin");
        await File.WriteAllBytesAsync(externalFile, RandomNumberGenerator.GetBytes(128 * 1024));
        var password = "PHASE21V-RC1-" + Guid.NewGuid().ToString("N");
        var bootstrapSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provisioningEnvironment = new Dictionary<string, string>
        {
            ["HISPANIA_SECRET_PHASE21V_BOOTSTRAP"] = bootstrapSecret,
            ["HISPANIA_SECRET_PHASE21V_PASSWORD"] = password,
            // Migration acceptance does not exercise media processing. Keep the fixture
            // independent from optional FFmpeg binaries on hosted CI runners.
            ["Ffprobe__ExecutablePath"] = "",
            ["Ffmpeg__ExecutablePath"] = ""
        };
        try
        {
            using var fixture = new HostFixture(rc1, officialArtifactInstall: true);
            provisioningEnvironment["Persistence__Sqlite__Path"] = Path.Combine(fixture.Home, "state", "media.sqlite");
            fixture.LibraryRootOverride = externalRoot;
            var mediaDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(externalFile)));
            var secretPath = Path.Combine(fixture.Home, "secrets", "migration-preservation.canary");
            var secretBytes = RandomNumberGenerator.GetBytes(64);
            await File.WriteAllBytesAsync(secretPath, secretBytes);
            fixture.WriteConfiguration(new { libraryRoots = new[] { externalRoot }, bootstrapSecret = "env:PHASE21V_BOOTSTRAP", bootstrapPasswordSecret = "env:PHASE21V_PASSWORD", bootstrapUsername = "deployment-operator" });
            var provision = await fixture.CommandAsync(["--home", fixture.Home, "--bootstrap"], provisioningEnvironment);
            Assert.True(provision.Code == 0, "Official RC1 bootstrap failed: " + provision.Output);
            Assert.DoesNotContain(bootstrapSecret, provision.Output, StringComparison.Ordinal);
            var provisionedIdentity = new IdentityService(new SqliteIdentityStore(Path.Combine(fixture.Home, "state", "platform", "identity.sqlite")));
            Assert.NotNull(await provisionedIdentity.GetUserByUsernameAsync("deployment-operator"));
            Assert.True((await provisionedIdentity.LoginAsync("deployment-operator", password)).Succeeded, "The official RC1 bootstrap output did not produce credentials accepted by its identity store.");
            using (var source = fixture.Start(environment: provisioningEnvironment))
            {
                await fixture.ReadyAsync(source);
                var session = await LoginForSessionAsync(fixture.Client, password);
                using (var rolesRequest = new HttpRequestMessage(HttpMethod.Get, "/api/authorization/roles"))
                {
                    rolesRequest.Headers.Authorization = new AuthenticationHeaderValue("Session", session);
                    using var rolesResponse = await fixture.Client.SendAsync(rolesRequest); rolesResponse.EnsureSuccessStatusCode();
                    using var roles = JsonDocument.Parse(await rolesResponse.Content.ReadAsStringAsync());
                    var administratorRoleId = roles.RootElement.EnumerateArray().Single(role => role.GetProperty("role").GetProperty("name").GetString() == "administrator").GetProperty("role").GetProperty("id").GetProperty("value").GetGuid();
                    using var grant = new HttpRequestMessage(HttpMethod.Put, $"/api/authorization/roles/{administratorRoleId:D}/permissions/inventory.write");
                    grant.Headers.Authorization = new AuthenticationHeaderValue("Session", session);
                    using var granted = await fixture.Client.SendAsync(grant); Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
                }
                using var createItem = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/items")
                {
                    Content = JsonContent.Create(new { sku = "MIGRATION-PRESERVE-1", name = "Retained inventory record" })
                };
                createItem.Headers.Authorization = new AuthenticationHeaderValue("Session", session);
                using var createdItem = await fixture.Client.SendAsync(createItem);
                Assert.Equal(HttpStatusCode.Created, createdItem.StatusCode);
                Assert.Throws<DeploymentException>(() => InstallationOperations.PlanCompleteRc1ToPlatformOnly(platform, fixture.Home));
                await fixture.StopAsync(source);
            }
            // Seed a populated historical Media catalog from the immutable 0.7 compatibility
            // corpus. The fixture lives under this disposable HOME and is never opened by the
            // developer's real profile or production installation.
            var mediaDatabasePath = Path.Combine(fixture.Home, "state", "media.sqlite");
            SqliteConnection.ClearAllPools();
            File.Copy(Path.Combine(FindRepositoryRoot(), "compatibility", "data", "0.7", "media-library.sqlite"), mediaDatabasePath, overwrite: true);
            var mediaDatabaseHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mediaDatabasePath)));
            Assert.True(await CountSqliteRowsAsync(mediaDatabasePath, "media_entities") > 0, "Historical Media fixture must contain media entities.");
            Assert.True(await CountSqliteRowsAsync(mediaDatabasePath, "metadata_assertions") > 0, "Historical Media fixture must contain populated metadata.");
            await AssertSqliteIntegrityAsync(mediaDatabasePath);
            var moduleDatabasePaths = new[]
            {
                Path.Combine(fixture.Home, "state", "inventory.sqlite")
            };
            Assert.All(moduleDatabasePaths, path => Assert.True(File.Exists(path), "Complete RC1 did not initialize expected module database " + path));
            var moduleDatabaseHashes = moduleDatabasePaths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
            Assert.Equal(1L, await CountInventoryItemAsync(moduleDatabasePaths[0], "MIGRATION-PRESERVE-1"));
            var sourceManifestSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Home, "app", "artifact-manifest.json"))));
            await AssertSqliteIntegrityAsync(moduleDatabasePaths[0]);
            await Assert.ThrowsAsync<DeploymentException>(() => InstallationOperations.UpgradeAsync(platform, fixture.Home));
            Assert.Equal(sourceManifestSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Home, "app", "artifact-manifest.json")))));
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app.old")));

            var targetAssembly = Path.Combine(platform, "server", "Hispania.Platform.Server.dll");
            var targetAssemblyBytes = await File.ReadAllBytesAsync(targetAssembly);
            try
            {
                targetAssemblyBytes[0] ^= 0x01;
                await File.WriteAllBytesAsync(targetAssembly, targetAssemblyBytes);
                Assert.Throws<DeploymentException>(() => InstallationOperations.PlanCompleteRc1ToPlatformOnly(platform, fixture.Home));
            }
            finally
            {
                targetAssemblyBytes[0] ^= 0x01;
                await File.WriteAllBytesAsync(targetAssembly, targetAssemblyBytes);
            }
            Assert.Equal(sourceManifestSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Home, "app", "artifact-manifest.json")))));

            var installationRecordPath = Path.Combine(fixture.Home, "config", "installation.json");
            var originalInstallationRecord = await File.ReadAllBytesAsync(installationRecordPath);
            var mismatchedRecord = JsonNode.Parse(originalInstallationRecord)!.AsObject();
            mismatchedRecord["gitSha"] = new string('0', 40);
            await File.WriteAllTextAsync(installationRecordPath, mismatchedRecord.ToJsonString());
            Assert.Throws<DeploymentException>(() => InstallationOperations.PlanCompleteRc1ToPlatformOnly(platform, fixture.Home));
            var targetManifestShaForNegativeCase = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(platform, "artifact-manifest.json"))));
            await Assert.ThrowsAsync<DeploymentException>(() => InstallationOperations.MigrateCompleteRc1ToPlatformOnlyAsync(platform, fixture.Home, targetManifestShaForNegativeCase));
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app.new")));
            await File.WriteAllBytesAsync(installationRecordPath, originalInstallationRecord);

            var originalConfiguration = await File.ReadAllBytesAsync(Path.Combine(fixture.Home, "config", "production.json"));
            fixture.WriteConfiguration(new
            {
                libraryRoots = new[] { externalRoot }, persistence = "postgresql",
                postgreSql = new { host = "127.0.0.1", database = "hispania", username = "hispania", passwordSecret = "env:PHASE21V_PG_PASSWORD" }
            });
            Assert.Throws<DeploymentException>(() => InstallationOperations.PlanCompleteRc1ToPlatformOnly(platform, fixture.Home));
            var targetManifestSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(platform, "artifact-manifest.json"))));
            await Assert.ThrowsAsync<DeploymentException>(() => InstallationOperations.MigrateCompleteRc1ToPlatformOnlyAsync(platform, fixture.Home, targetManifestSha));
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app.new")));
            Assert.Equal(sourceManifestSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Home, "app", "artifact-manifest.json")))));
            await File.WriteAllBytesAsync(Path.Combine(fixture.Home, "config", "production.json"), originalConfiguration);

            var plan = InstallationOperations.PlanCompleteRc1ToPlatformOnly(platform, fixture.Home);
            Assert.Equal("0.7.0-rc.1", plan.SourceVersion);
            Assert.Equal("complete", ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).DistributionType);
            Assert.Contains(Path.GetFullPath(externalRoot), plan.PreservedExternalPaths);
            var serializedPlan = JsonSerializer.Serialize(plan);
            Assert.DoesNotContain(bootstrapSecret, serializedPlan, StringComparison.Ordinal);
            Assert.DoesNotContain(password, serializedPlan, StringComparison.Ordinal);
            var planCommand = await RunDeploymentCliAsync(platform, "plan-migrate-complete-rc1-to-platform-only", platform, fixture.Home);
            Assert.Equal(0, planCommand.Code);
            Assert.DoesNotContain(bootstrapSecret, planCommand.Output, StringComparison.Ordinal);
            Console.WriteLine("Inspected migration plan: " + planCommand.Output);
            using var cliPlan = JsonDocument.Parse(planCommand.Output);
            var confirmedManifestSha = cliPlan.RootElement.GetProperty("targetManifestSha256").GetString()!;
            Assert.Equal(plan.TargetManifestSha256, confirmedManifestSha, ignoreCase: true);
            Console.WriteLine("Confirmed target manifest SHA-256: " + confirmedManifestSha);
            await AssertAbruptMigrationInterruptionRecoversAsync(platform, fixture.Home, confirmedManifestSha, sourceManifestSha);
            await AssertMigrationFixtureRestoredAsync(fixture.Home, sourceManifestSha, moduleDatabasePaths[0], moduleDatabaseHashes[0], mediaDatabasePath, mediaDatabaseHash, secretPath, secretBytes, externalFile, mediaDigest);
            foreach (var checkpoint in new[]
            {
                "staging-file-copied", "before-backup", "after-backup-create", "during-state-verification",
                "after-backup-verified", "before-switch", "during-switch-after-old-moved", "after-switch", "after-metadata-write"
            })
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => InstallationOperations.MigrateCompleteRc1ToPlatformOnlyWithCheckpointsAsync(
                    platform, fixture.Home, confirmedManifestSha, reached =>
                    {
                        if (reached == checkpoint) throw new InvalidOperationException("Controlled migration exception: " + checkpoint);
                    }));
                InstallationOperations.RecoverBeforeStartup(fixture.Home);
                await AssertMigrationFixtureRestoredAsync(fixture.Home, sourceManifestSha, moduleDatabasePaths[0], moduleDatabaseHashes[0], mediaDatabasePath, mediaDatabaseHash, secretPath, secretBytes, externalFile, mediaDigest);
                Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app.new")));
                Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app.old")));
            }
            var migrationCommand = await RunDeploymentCliAsync(platform, "migrate-complete-rc1-to-platform-only", platform, fixture.Home, confirmedManifestSha);
            Assert.Equal(0, migrationCommand.Code);
            Console.WriteLine("Migration output: " + migrationCommand.Output.Trim());
            var bundle = migrationCommand.Output.Trim();
            Assert.True(Path.IsPathFullyQualified(bundle));
            Assert.True(File.Exists(Path.Combine(bundle, "manifest.json")));
            Assert.Equal("platform-only", ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).DistributionType);
            Assert.Equal(secretBytes, await File.ReadAllBytesAsync(secretPath));
            Assert.Equal(mediaDigest, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(externalFile))));
            Assert.Equal(mediaDatabaseHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mediaDatabasePath))));
            await AssertSqliteIntegrityAsync(mediaDatabasePath);
            Assert.Equal(moduleDatabaseHashes, moduleDatabasePaths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
            await AssertSqliteIntegrityAsync(moduleDatabasePaths[0]);

            using (var migrated = fixture.Start(environment: provisioningEnvironment))
            {
                await fixture.ReadyAsync(migrated);
                await AssertLoginAsync(fixture.Client, password);
                using var inventoryEndpoint = await fixture.Client.GetAsync("/api/inventory/items");
                Assert.Equal(HttpStatusCode.NotFound, inventoryEndpoint.StatusCode);
                await fixture.StopAsync(migrated);
            }
            Assert.Equal(moduleDatabaseHashes, moduleDatabasePaths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray());
            Assert.Equal(mediaDatabaseHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(mediaDatabasePath))));
            Assert.Equal(1L, await CountInventoryItemAsync(moduleDatabasePaths[0], "MIGRATION-PRESERVE-1"));
            var repeatedMigration = await RunDeploymentCliAsync(platform, "migrate-complete-rc1-to-platform-only", platform, fixture.Home, confirmedManifestSha);
            Assert.Equal(0, repeatedMigration.Code);
            Assert.Equal(bundle, repeatedMigration.Output.Trim());

            SqliteConnection.ClearAllPools();
            await Assert.ThrowsAsync<InvalidOperationException>(() => InstallationOperations.RestoreRollbackWithCheckpointsAsync(
                fixture.Home, bundle, checkpoint =>
                {
                    if (checkpoint == "rollback-state-moved") throw new InvalidOperationException("Controlled rollback interruption after state was retained.");
                }));
            InstallationOperations.RecoverBeforeStartup(fixture.Home);
            Assert.Equal("complete", ArtifactManifest.Verify(Path.Combine(fixture.Home, "app")).DistributionType);
            await AssertSqliteIntegrityAsync(mediaDatabasePath);
            Assert.True(await CountSqliteRowsAsync(mediaDatabasePath, "media_entities") > 0);
            Assert.True(await CountSqliteRowsAsync(mediaDatabasePath, "metadata_assertions") > 0);
            using var rolledBack = fixture.Start(environment: provisioningEnvironment);
            await fixture.ReadyAsync(rolledBack);
            await AssertLoginAsync(fixture.Client, password);
            await fixture.StopAsync(rolledBack);
        }
        finally { Directory.Delete(externalRoot, recursive: true); }
    }

    [Fact]
    public void UninstallPreservesConfigurationSecretsStateAndBackups()
    {
        using var fixture = new HostFixture();
        foreach (var directory in new[] { "secrets", "state", "backups" }) File.WriteAllText(Path.Combine(fixture.Home, directory, "retain.txt"), directory);
        InstallationOperations.UninstallBinaries(fixture.Home);
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, "app")));
        foreach (var directory in new[] { "secrets", "state", "backups" }) Assert.Equal(directory, File.ReadAllText(Path.Combine(fixture.Home, directory, "retain.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Home, "config", "production.json")));
    }

    [Theory]
    [InlineData("staging")]
    [InlineData("switching")]
    public async Task InterruptedStagingOrSwitchBlocksStartupAndExplicitlyRecoversVerifiedWholeArtifact(string phase)
    {
        using var fixture = new HostFixture();
        var marker = Path.Combine(fixture.Home, "run", "upgrade-in-progress.json");
        if (phase == "staging")
        {
            Directory.CreateDirectory(Path.Combine(fixture.Home, "app.new"));
            File.WriteAllText(Path.Combine(fixture.Home, "app.new", "partial.txt"), "interrupted staged payload");
        }
        else Directory.Move(Path.Combine(fixture.Home, "app"), Path.Combine(fixture.Home, "app.old"));
        File.WriteAllText(marker, JsonSerializer.Serialize(new { phase, backup = "preserved outside the binary switch" }));
        if (phase == "staging") Assert.Equal(6, (await fixture.CommandAsync(["--home", fixture.Home, "--check"])).Code);
        else Assert.Throws<DeploymentException>(() => ProductionInstallation.Load(["--home", fixture.Home], "server"));
        InstallationOperations.RecoverBeforeStartup(fixture.Home);
        Assert.False(File.Exists(marker)); _ = ArtifactManifest.Verify(Path.Combine(fixture.Home, "app"));
        using var server = fixture.Start(); await fixture.ReadyAsync(server); await fixture.StopAsync(server);
    }

    [Fact]
    public async Task DurableInstallationVersionMismatchRejectsPreflightWithoutRewritingEvidence()
    {
        using var fixture = new HostFixture();
        var path = Path.Combine(fixture.Home, "config", "installation.json");
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.NotNull(metadata["installedUtc"]); metadata["gitSha"] = new string('0', 40); File.WriteAllText(path, metadata.ToJsonString());
        var before = File.ReadAllBytes(path);
        Assert.Equal(6, (await fixture.CommandAsync(["--home", fixture.Home, "--check"])).Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    internal static string Artifact => Environment.GetEnvironmentVariable("HISPANIA_PHASE18_ARTIFACT") ?? throw new InvalidOperationException("Set HISPANIA_PHASE18_ARTIFACT to a verified publication; no source-tree fallback is allowed.");
    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
    private static async Task AssertLoginAsync(HttpClient client, string password)
        => _ = await LoginForSessionAsync(client, password);

    private static async Task<string> LoginForSessionAsync(HttpClient client, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/identity/login", new { username = "deployment-operator", password });
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected login success, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("sessionToken").GetString()!;
    }
    private static async Task AssertSqliteIntegrityAsync(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }
    private static async Task<long> CountInventoryItemAsync(string path, string sku)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM items WHERE sku=$sku"; command.Parameters.AddWithValue("$sku", sku);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static async Task<long> CountSqliteRowsAsync(string path, string table)
    {
        if (table is not ("media_entities" or "metadata_assertions")) throw new ArgumentOutOfRangeException(nameof(table));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Hispania.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root was not found for the disposable migration fixture.");
    }
    private static async Task AssertMigrationFixtureRestoredAsync(string home, string sourceManifestSha, string inventoryPath, string inventoryHash, string mediaPath, string mediaHash, string secretPath, byte[] secretBytes, string externalPath, string externalHash)
    {
        Assert.Equal(sourceManifestSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(home, "app", "artifact-manifest.json")))));
        Assert.Equal(inventoryHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inventoryPath))));
        Assert.Equal(mediaHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mediaPath))));
        Assert.Equal(secretBytes, File.ReadAllBytes(secretPath));
        Assert.Equal(externalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(externalPath))));
        var identity = new IdentityService(new SqliteIdentityStore(Path.Combine(home, "state", "platform", "identity.sqlite")));
        Assert.NotNull(await identity.GetUserByUsernameAsync("deployment-operator"));
        using var inventoryConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = inventoryPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        inventoryConnection.Open(); using var inventoryCommand = inventoryConnection.CreateCommand(); inventoryCommand.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", inventoryCommand.ExecuteScalar());
        using var mediaConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = mediaPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        mediaConnection.Open(); using var mediaCommand = mediaConnection.CreateCommand(); mediaCommand.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", mediaCommand.ExecuteScalar());
    }
    private static async Task AssertAbruptMigrationInterruptionRecoversAsync(string artifact, string home, string targetManifestSha, string sourceManifestSha)
    {
        var helper = Path.Combine(FindRepositoryRoot(), "tests", "deployment-migration-harness", "bin", "Release", "net10.0", "Hispania.Deployment.MigrationHarness.dll");
        Assert.True(File.Exists(helper), "The migration crash harness must be built as a test dependency.");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(helper); start.ArgumentList.Add(artifact); start.ArgumentList.Add(home); start.ArgumentList.Add(targetManifestSha); start.ArgumentList.Add("staging-file-copied");
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated migration crash harness.");
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }
        var output = (await stdout) + (await stderr);
        Assert.NotEqual(0, child.ExitCode);
        Assert.Contains("Controlled test-only process termination", output, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(home, "app.new")), "Abrupt staging termination must leave recoverable evidence.");
        InstallationOperations.RecoverBeforeStartup(home);
        Assert.Equal(sourceManifestSha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(home, "app", "artifact-manifest.json")))));
        Assert.False(Directory.Exists(Path.Combine(home, "app.new")));
        Assert.False(Directory.Exists(Path.Combine(home, "app.old")));
        Console.WriteLine("Abrupt child termination at staging-file-copied was recovered; child exit code: " + child.ExitCode);
    }
    private static async Task<(int Code, string Output)> RunDeploymentCliAsync(string artifact, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(artifact, "deployment", "Hispania.Deployment.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the candidate Deployment CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { if (!process.HasExited) process.Kill(true); throw new TimeoutException("Candidate Deployment CLI exceeded 90 seconds."); }
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    internal sealed class HostFixture : IDisposable
    {
        internal string Home { get; }
        internal string RunId { get; }
        internal int Port { get; }
        internal HttpClient Client { get; }
        internal string? LibraryRootOverride { get; set; }
        private readonly List<Process> processes = [];
        private readonly List<(int Id, DateTime Started)> identities = [];
        private readonly Dictionary<int, (ConcurrentQueue<string> Output, ConcurrentQueue<string> Error)> processLogs = [];
        internal HostFixture(string? artifact = null, string? directoryRoot = null, string role = "server", bool officialArtifactInstall = false)
        {
            RunId = Guid.NewGuid().ToString("N");
            Home = Path.Combine(directoryRoot ?? Path.GetTempPath(), "hispania-phase18 isolated ñ " + RunId);
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (officialArtifactInstall)
            {
                var deployment = Path.Combine(artifact ?? Artifact, "deployment", "Hispania.Deployment.dll");
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(deployment); start.ArgumentList.Add("install"); start.ArgumentList.Add(artifact ?? Artifact); start.ArgumentList.Add(Home); start.ArgumentList.Add(role);
                using var install = Process.Start(start) ?? throw new InvalidOperationException("Could not start the official RC1 installer.");
                var output = install.StandardOutput.ReadToEnd() + install.StandardError.ReadToEnd(); install.WaitForExit();
                Assert.True(install.ExitCode == 0, "Official RC1 installation failed: " + output);
            }
            else InstallationOperations.Install(artifact ?? Artifact, Home, role);
            // Application requests include deliberate 600,000-iteration PBKDF2 password verification.
            // The short readiness polling budget must not become an authentication performance SLA.
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(30) };
            WriteConfiguration(new { });
        }
        internal void WriteConfiguration(object options)
        {
            var json = JsonSerializer.SerializeToNode(options)!.AsObject(); json["endpoint"] = $"http://127.0.0.1:{Port}";
            File.WriteAllText(Path.Combine(Home, "config", "production.json"), json.ToJsonString());
        }
        internal Process Start(string[]? arguments = null, Dictionary<string, string>? environment = null, bool worker = false)
        {
            var applicationDirectory = Path.Combine(Home, "app", worker ? "worker" : "server");
            var workingDirectory = worker ? Path.GetTempPath() : applicationDirectory;
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
            var manifest = ArtifactManifest.Verify(Path.Combine(Home, "app"));
            var assembly = worker ? "Hispania.WorkerAgent.dll" : manifest.DistributionType == "platform-only" ? "Hispania.Platform.Server.dll" : "Hispania.Server.dll";
            start.ArgumentList.Add(Path.Combine(applicationDirectory, assembly));
            // The archived Phase 17 development host does not understand --home and ignores
            // the fixture's production.json endpoint. Pin every host process through host config.
            start.Environment["ASPNETCORE_URLS"] = Client.BaseAddress!.ToString();
            foreach (var argument in arguments ?? ["--home", Home]) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("HISPANIA__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HISPANIA_SECRET_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            foreach (var (key, value) in CreateIsolatedDataEnvironment(Home, LibraryRootOverride)) start.Environment[key] = value;
            if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var output = new ConcurrentQueue<string>(); var error = new ConcurrentQueue<string>();
            if (!worker)
            {
                process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) output.Enqueue(eventArgs.Data); };
                process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) error.Enqueue(eventArgs.Data); };
            }
            if (!process.Start()) throw new InvalidOperationException("Could not start isolated test host process.");
            if (!worker)
            {
                process.BeginOutputReadLine(); process.BeginErrorReadLine(); processLogs.Add(process.Id, (output, error));
            }
            processes.Add(process); identities.Add((process.Id, process.StartTime)); return process;
        }
        private static Dictionary<string, string> CreateIsolatedDataEnvironment(string home, string? libraryRootOverride)
        {
            var state = Path.Combine(home, "state");
            var profile = Path.Combine(home, ".profile");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Platform__DataPath"] = Path.Combine(state, "platform"),
                ["Inventory__Sqlite__Path"] = Path.Combine(state, "inventory.sqlite"),
                ["DistributedWorkers__StatePath"] = Path.Combine(state, "distributed-coordinator.sqlite"),
                ["DistributedWorkers__TransferPath"] = Path.Combine(state, "distributed-transfers"),
                ["Persistence__Sqlite__Path"] = Path.Combine(state, "catalog.sqlite"),
                ["Storage__Providers__Filesystem__Root"] = Path.Combine(state, "assets"),
                ["Playback__TranscodeCachePath"] = Path.Combine(state, "transcoded-media"),
                ["PlaybackExperiments__StatePath"] = Path.Combine(state, "playback.json"),
                ["Metadata__InventoryPath"] = Path.Combine(state, "metadata-index.json"),
                ["Modules__ExtensionsPath"] = Path.Combine(state, "extensions"),
                ["Modules__ArchitectureDemonstration"] = "hispania.architecture-test.a",
                ["Library__Roots__0__Key"] = "fixture",
                ["Library__Roots__0__Path"] = Path.Combine(home, "library-source"),
                ["Ffprobe__ExecutablePath"] = "ffprobe",
                ["Ffmpeg__ExecutablePath"] = "ffmpeg",
                ["Ffmpeg__CachePath"] = Path.Combine(state, "ffmpeg-cache"),
                ["Backup__ManagedRoot"] = state,
                ["USERPROFILE"] = profile,
                ["HOME"] = profile,
                ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"),
                ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
                ["DOTNET_CLI_HOME"] = Path.Combine(profile, ".dotnet"),
                ["TEMP"] = Path.Combine(profile, "Temp"),
                ["TMP"] = Path.Combine(profile, "Temp")
            };
            if (libraryRootOverride is not null) values["Library__Roots__0__Path"] = libraryRootOverride;
            foreach (var (key, path) in values.Where(pair => pair.Key != "Library__Roots__0__Path" && Path.IsPathRooted(pair.Value)).ToArray())
            {
                var fullPath = Path.GetFullPath(path);
                var prefix = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!fullPath.Equals(Path.GetFullPath(home), comparison) && !fullPath.StartsWith(prefix, comparison))
                    throw new InvalidOperationException($"Test data path {key} escapes the temporary host directory.");
                values[key] = fullPath;
            }
            foreach (var key in new[] { "LOCALAPPDATA", "APPDATA", "DOTNET_CLI_HOME", "TEMP" }) Directory.CreateDirectory(values[key]);
            return values;
        }
        internal async Task<(int Code, string Output)> CommandAsync(string[] arguments, Dictionary<string, string>? environment = null)
        {
            using var process = Start(arguments, environment);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                throw new TimeoutException("Host command exceeded its 60-second limit: " + FormatProcessLogs(process), exception);
            }
            return (process.ExitCode, FormatProcessLogs(process));
        }
        internal async Task ReadyAsync(Process process)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException("Host exited: " + FormatProcessLogs(process));
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { using var response = await Client.GetAsync("/readyz", probeTimeout.Token); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested) { }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Artifact failed to become Ready within 45 seconds: " + FormatProcessLogs(process));
        }
        private string FormatProcessLogs(Process process)
        {
            if (!processLogs.TryGetValue(process.Id, out var logs)) return "No process output was captured.";
            return "stdout: " + string.Join(Environment.NewLine, logs.Output.TakeLast(40)) + Environment.NewLine +
                "stderr: " + string.Join(Environment.NewLine, logs.Error.TakeLast(80));
        }
        internal static async Task StopProcessAsync(Process process)
        {
            if (process.HasExited) return;
            if (OperatingSystem.IsLinux())
            {
                using var signal = Process.Start(new ProcessStartInfo("kill") { UseShellExecute = false, ArgumentList = { "-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) } })!;
                await signal.WaitForExitAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
            }
            else { process.Kill(true); await process.WaitForExitAsync(); } // Actual Windows graceful stop is a separate mandatory SCM gate.
        }
        internal Task StopAsync(Process process)
        { Assert.Contains(process, processes); return StopProcessAsync(process); }
        public void Dispose()
        {
            foreach (var identity in identities)
            {
                try
                {
                    using var owned = Process.GetProcessById(identity.Id);
                    if (owned.StartTime == identity.Started && !owned.HasExited) { owned.Kill(true); owned.WaitForExit(10000); }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Client.Dispose(); SqliteConnection.ClearAllPools();
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(Home, true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }
    }
}
