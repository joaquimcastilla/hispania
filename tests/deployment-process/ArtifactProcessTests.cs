using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hispania.Hosting;
using Microsoft.Data.Sqlite;
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
        Assert.Equal("c3a827a4b828cff37785884e889689af7cc5fce2", ArtifactManifest.Verify(predecessor).GitSha);
        using var fixture = new HostFixture(predecessor);
        var password = "PHASE18_SUPER_SECRET_CANARY_UPGRADE_" + Guid.NewGuid().ToString("N");
        var legacyBearer = Guid.NewGuid().ToString("N");
        var legacyArgs = new[] { $"--contentRoot={Path.Combine(fixture.Home, "app", "server")}", $"--webroot={Path.Combine(fixture.Home, "app", "server", "wwwroot", "modern")}", $"--urls=http://127.0.0.1:{fixture.Port}", $"--Backup:ManagedRoot={Path.Combine(fixture.Home, "state")}", $"--Library:Roots:0:Key=legacy", $"--Library:Roots:0:Path={Path.Combine(fixture.Home, "library-source")}", "--Ffprobe:ExecutablePath=ffprobe", "--Ffmpeg:ExecutablePath=ffmpeg", "--Modules:ArchitectureDemonstration=hispania.architecture-test.a", "--Modules:RequirePublisherSignature=true" };
        Directory.CreateDirectory(Path.Combine(fixture.Home, "library-source"));
        using (var server = fixture.Start(legacyArgs, new() { ["Modules__AdminToken"] = legacyBearer }))
        {
            await fixture.ReadyAsync(server);
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/identity/users") { Content = JsonContent.Create(new { username = "deployment-operator", displayName = "Upgrade operator", password }) };
            create.Headers.Authorization = new("Bearer", legacyBearer);
            using var created = await fixture.Client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            await AssertLoginAsync(fixture.Client, password);
            using var web = await fixture.Client.GetAsync("/"); Assert.Equal(HttpStatusCode.OK, web.StatusCode);
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
    {
        using var response = await client.PostAsJsonAsync("/api/identity/login", new { username = "deployment-operator", password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal sealed class HostFixture : IDisposable
    {
        internal string Home { get; }
        internal int Port { get; }
        internal HttpClient Client { get; }
        private readonly List<Process> processes = [];
        private readonly List<(int Id, DateTime Started)> identities = [];
        internal HostFixture(string? artifact = null, string? directoryRoot = null)
        {
            Home = Path.Combine(directoryRoot ?? Path.GetTempPath(), "hispania-phase18 isolated ñ " + Guid.NewGuid().ToString("N"));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            InstallationOperations.Install(artifact ?? Artifact, Home, "server");
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
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
            start.ArgumentList.Add(Path.Combine(Home, "app", worker ? "worker" : "server", worker ? "Hispania.WorkerAgent.dll" : "Hispania.Server.dll"));
            foreach (var argument in arguments ?? ["--home", Home]) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("HISPANIA__", StringComparison.OrdinalIgnoreCase) || key.StartsWith("HISPANIA_SECRET_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
            if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
            var process = Process.Start(start)!; processes.Add(process); identities.Add((process.Id, process.StartTime)); return process;
        }
        internal async Task<(int Code, string Output)> CommandAsync(string[] arguments, Dictionary<string, string>? environment = null)
        {
            using var process = Start(arguments, environment);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }
        internal async Task ReadyAsync(Process process)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (process.HasExited) throw new InvalidOperationException("Host exited: " + await process.StandardError.ReadToEndAsync() + await process.StandardOutput.ReadToEndAsync());
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { using var response = await Client.GetAsync("/readyz", probeTimeout.Token); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (probeTimeout.IsCancellationRequested) { }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Artifact failed to become Ready within 45 seconds.");
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
