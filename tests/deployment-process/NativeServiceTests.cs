using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Hispania.Deployment.ProcessTests;

[Trait("Category", "Phase18NativeService")]
public sealed class NativeServiceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("server")]
    [InlineData("worker")]
    public async Task NativeServiceActuallyInstallsStartsRestartsStopsAndUninstallsWithoutPurgingState(string role)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("HISPANIA_PHASE18_REQUIRE_SERVICE"));
        // systemd PrivateTmp intentionally hides /tmp; use the runner's explicit service root.
        var root = Environment.GetEnvironmentVariable("HISPANIA_PHASE18_SERVICE_ROOT") ?? throw new InvalidOperationException("Native service gate requires a provisioned service root outside private runner directories and /tmp.");
        using var fixture = new ArtifactProcessTests.HostFixture(directoryRoot: root);
        var canary = "PHASE18_SUPER_SECRET_CANARY_NATIVE_" + Guid.NewGuid().ToString("N");
        var canaryPath = Path.Combine(fixture.Home, "secrets", "NATIVE_CANARY.secret");
        File.WriteAllText(canaryPath, canary); WorkerDeploymentTests.Protect(canaryPath);
        var retainedBackup = Path.Combine(fixture.Home, "backups", "native-retain.txt");
        File.WriteAllText(retainedBackup, "retained backup evidence");
        fixture.WriteConfiguration(new { bootstrapSecret = "file:NATIVE_CANARY" });
        using var serverCertificate = WorkerDeploymentTests.Certificate("native-coordinator");
        using var clientCertificate = WorkerDeploymentTests.Certificate("native-worker");
        WebApplication? coordinator = null;
        var negotiations = 0;
        if (role == "worker")
        {
            File.WriteAllBytes(Path.Combine(fixture.Home, "secrets", "client.pfx"), clientCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, canary));
            WorkerDeploymentTests.Protect(Path.Combine(fixture.Home, "secrets", "client.pfx"));
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(fixture.Port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate; https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => certificate.Thumbprint == clientCertificate.Thumbprint;
            })));
            coordinator = builder.Build();
            coordinator.MapPost("/internal/worker/v1/native-worker/connect", () => { Interlocked.Increment(ref negotiations); return Results.Ok(new { selectedProtocolVersion = 1, authorizedCapabilities = Array.Empty<object>() }); });
            coordinator.MapPost("/internal/worker/v1/native-worker/jobs/lease", () => Results.NoContent());
            coordinator.MapPost("/internal/worker/v1/native-worker/heartbeat", () => Results.Ok(new { cancelledJobIds = Array.Empty<string>() }));
            coordinator.MapPost("/internal/worker/v1/native-worker/disconnect", () => Results.NoContent());
            await coordinator.StartAsync();
            fixture.WriteConfiguration(new { role, worker = new { coordinatorUri = $"https://localhost:{fixture.Port}", nodeId = "native-worker", certificateFile = "client.pfx", passwordSecret = "file:NATIVE_CANARY", coordinatorCertificateSha256Pins = new[] { Convert.ToHexString(SHA256.HashData(serverCertificate.RawData)) } } });
        }
        var installed = false;
        try
        {
            await ServiceAsync("install", fixture.Home, role); installed = true;
            if (OperatingSystem.IsWindows())
            {
                var name = role == "server" ? "HispaniaServer" : "HispaniaWorker";
                var definition = await RunAsync("sc.exe", ["qc", name]);
                Assert.Contains("NT AUTHORITY\\LocalService", definition, StringComparison.OrdinalIgnoreCase);
                await RunAsync("sc.exe", ["qfailure", name]);
                WindowsServiceRecovery.AssertConfigured(name);
                output.WriteLine("SCM definition: LocalService confirmed; recovery definition validated; crash recovery runtime not exercised.");
            }
            else
            {
                var identity = await RunAsync("systemctl", ["show", "hispania-" + role + ".service", "--property=User", "--value"]);
                Assert.Equal("hispania", identity.Trim());
                var uid = await RunAsync("id", ["-u", "hispania"]);
                Assert.NotEqual("0", uid.Trim());
                output.WriteLine("systemd definition: non-root hispania identity confirmed; app directory read-only for service user.");
            }
            await ServiceAsync("start", fixture.Home, role); await WaitAsync(1);
            await LivenessAsync();
            await ServiceAsync("status", fixture.Home, role);
            await ServiceAsync("restart", fixture.Home, role); await WaitAsync(2);
            await LivenessAsync();
            var timer = Stopwatch.StartNew(); await ServiceAsync("stop", fixture.Home, role);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(40));
            output.WriteLine("{0} {1}: native start/readiness/restart passed; graceful stop {2:F3}s.", OperatingSystem.IsWindows() ? "SCM" : "systemd", role, timer.Elapsed.TotalSeconds);
            if (OperatingSystem.IsLinux())
                Assert.DoesNotContain(canary, await NativeOperationAsync("journal", fixture.Home, role, () => RunAsync("sudo", ["journalctl", "--unit=hispania-" + role + ".service", "--no-pager"])), StringComparison.Ordinal);
            else
                foreach (var log in Directory.EnumerateFiles(Path.Combine(fixture.Home, "logs"), "*", SearchOption.AllDirectories)) Assert.DoesNotContain(canary, File.ReadAllText(log), StringComparison.Ordinal);
            using (Hispania.Hosting.InstallationOperations.Lock(fixture.Home)) { }
            await ServiceAsync("uninstall", fixture.Home, role); installed = false;
            Assert.True(File.Exists(Path.Combine(fixture.Home, "config", "production.json")));
            Assert.Equal("retained backup evidence", File.ReadAllText(retainedBackup));
            if (OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
                await NativeOperationAsync("retained-secret", fixture.Home, role, () => RunAsync("sudo", ["test", "-f", canaryPath]));
            else Assert.True(File.Exists(canaryPath));
            if (role == "server") Assert.True(File.Exists(Path.Combine(fixture.Home, "state", "platform", "identity.sqlite")));
        }
        catch
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var log in Directory.EnumerateFiles(Path.Combine(fixture.Home, "logs"), "*", SearchOption.AllDirectories)) output.WriteLine(File.ReadAllText(log));
                output.WriteLine(await RunAsync("pwsh", ["-NoProfile", "-Command", "Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Service Control Manager';StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Select-Object -First 12 TimeCreated,Id,Message | Format-List | Out-String; exit 0"]));
            }
            throw;
        }
        finally
        {
            if (installed) await ServiceAsync("uninstall", fixture.Home, role);
            if (coordinator is not null) { await coordinator.StopAsync(); await coordinator.DisposeAsync(); }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
            {
                await NativeOperationAsync("reclaim", fixture.Home, role, MissingNativeHelperAsync);
            }
            else if (OperatingSystem.IsLinux())
            {
                var uid = await RunAsync("id", ["-u"]); var gid = await RunAsync("id", ["-g"]);
                await RunAsync("sudo", ["chown", "-R", uid.Trim() + ":" + gid.Trim(), fixture.Home]);
                await RunAsync("chmod", ["-R", "u+rwX", fixture.Home]);
            }
            else
            {
                // Restore fixture ownership after validating service-only access to private files.
                await RunAsync("icacls.exe", [Path.Combine(fixture.Home, "secrets"), "/reset", "/T", "/Q"]);
            }
        }
        async Task WaitAsync(int expected)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (role == "worker" && Volatile.Read(ref negotiations) >= expected) return;
                if (role == "server") try { using var response = await fixture.Client.GetAsync("/readyz"); if (response.IsSuccessStatusCode) return; } catch (HttpRequestException) { }
                await Task.Delay(200);
            }
            if (OperatingSystem.IsLinux())
                output.WriteLine(await NativeOperationAsync("journal", fixture.Home, role, () => RunAsync("sudo", ["journalctl", "--unit=hispania-" + role + ".service", "--no-pager", "--lines=80"])));
            throw new InvalidOperationException("Native service did not become Ready or negotiate within its startup deadline.");
        }
        async Task LivenessAsync()
        {
            if (role != "server") return;
            using var response = await fixture.Client.GetAsync("/healthz");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task ServiceAsync(string action, string home, string role)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
        {
            if (OperatingSystem.IsLinux() && action == "install") await RunAsync("chmod", ["-R", "a-w", Path.Combine(home, "app")]);
            await NativeOperationAsync(action, home, role, () => throw new InvalidOperationException("CI native helper required."));
            return;
        }
        var operations = Path.Combine(ArtifactProcessTests.Artifact, "operations");
        if (OperatingSystem.IsWindows()) await RunAsync("pwsh", ["-NoProfile", "-File", Path.Combine(operations, "windows-service.ps1"), "-Action", action, "-Home", home, "-Role", role]);
        else
        {
            if (action == "install") await RunAsync("chmod", ["-R", "a-w", Path.Combine(home, "app")]);
            await RunAsync("sudo", ["sh", Path.Combine(operations, "install-systemd.sh"), action, home, role, "hispania"]);
        }
    }

    private static Task<string> NativeOperationAsync(string action, string home, string role, Func<Task<string>> fallback)
    {
        var helper = Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER");
        if (string.IsNullOrEmpty(helper)) return fallback();
        return OperatingSystem.IsWindows()
            ? RunAsync("pwsh", ["-NoProfile", "-File", helper, "-Action", action, "-InstallationHome", home, "-Role", role])
            : RunAsync("sudo", ["-n", helper, action, home, role]);
    }

    private static Task<string> MissingNativeHelperAsync() => throw new InvalidOperationException("CI native helper required.");

    private static async Task<string> RunAsync(string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await process.WaitForExitAsync(timeout.Token);
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, command + " failed: " + output); return output;
    }
}
