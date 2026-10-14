using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
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
        using var fixture = new ArtifactProcessTests.HostFixture(directoryRoot: root, role: role);
        var serviceName = "Hispania-Test-" + fixture.RunId + (role == "server" ? "-Server" : "-Worker");
        fixture.WriteConfiguration(new { windowsServiceName = serviceName });
        var canary = "PHASE18_SUPER_SECRET_CANARY_NATIVE_" + Guid.NewGuid().ToString("N");
        var canaryPath = Path.Combine(fixture.Home, "secrets", "NATIVE_CANARY.secret");
        File.WriteAllText(canaryPath, canary); WorkerDeploymentTests.Protect(canaryPath);
        var adminPassword = "Native!" + Guid.NewGuid().ToString("N");
        if (role == "server")
        {
            fixture.WriteConfiguration(new { role, windowsServiceName = serviceName, bootstrapSecret = "file:NATIVE_CANARY", bootstrapPasswordSecret = "env:PHASE18_NATIVE_ADMIN_PASSWORD", bootstrapUsername = "deployment-operator" });
            var bootstrap = await fixture.CommandAsync(["--home", fixture.Home, "--bootstrap"], new() { ["HISPANIA_SECRET_PHASE18_NATIVE_ADMIN_PASSWORD"] = adminPassword });
            Assert.Equal(0, bootstrap.Code);
            Assert.DoesNotContain(canary, bootstrap.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(adminPassword, bootstrap.Output, StringComparison.Ordinal);
            // Remove bootstrap references before the service is installed or started.
            fixture.WriteConfiguration(new { role, windowsServiceName = serviceName });
        }
        var retainedBackup = Path.Combine(fixture.Home, "backups", "native-retain.txt");
        File.WriteAllText(retainedBackup, "retained backup evidence");
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
            fixture.WriteConfiguration(new { role, windowsServiceName = serviceName, worker = new { coordinatorUri = $"https://localhost:{fixture.Port}", nodeId = "native-worker", certificateFile = "client.pfx", passwordSecret = "file:NATIVE_CANARY", coordinatorCertificateSha256Pins = new[] { Convert.ToHexString(SHA256.HashData(serverCertificate.RawData)) } } });
        }
        var installed = false;
        try
        {
            if (OperatingSystem.IsWindows()) Assert.Equal("MISSING", await TryGetWindowsServiceAsync(serviceName));
            await ServiceAsync("install", fixture.Home, role, serviceName); installed = true;
            if (OperatingSystem.IsWindows())
            {
                var definition = await RunAsync("sc.exe", ["qc", serviceName]);
                Assert.Contains("NT AUTHORITY\\LocalService", definition, StringComparison.OrdinalIgnoreCase);
                await RunAsync("sc.exe", ["qfailure", serviceName]);
                await AssertOwnedWindowsServiceAsync(serviceName, fixture.Home, role);
                WindowsServiceRecovery.AssertConfigured(serviceName);
                output.WriteLine("SCM definition: LocalService confirmed; recovery actions validated; controlled crash will be checked after readiness.");
            }
            else
            {
                var identity = await RunAsync("systemctl", ["show", "hispania-" + role + ".service", "--property=User", "--value"]);
                Assert.Equal("hispania", identity.Trim());
                var uid = await RunAsync("id", ["-u", "hispania"]);
                Assert.NotEqual("0", uid.Trim());
                output.WriteLine("systemd definition: non-root hispania identity confirmed; app directory read-only for service user.");
            }
            await ServiceAsync("start", fixture.Home, role, serviceName); await WaitAsync(1);
            await LivenessAsync();
            if (OperatingSystem.IsWindows())
            {
                Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")), "Real SCM recovery requires the approved restricted privileged helper.");
                if (role == "server")
                {
                    using var initialLoginBody = new StringContent(JsonSerializer.Serialize(new { username = "deployment-operator", password = adminPassword }), System.Text.Encoding.UTF8, "application/json");
                    using var initialLogin = await fixture.Client.PostAsync("/api/identity/login", initialLoginBody);
                    Assert.Equal(System.Net.HttpStatusCode.OK, initialLogin.StatusCode);
                }
                var beforeCrash = await ReadWindowsServiceAsync(serviceName);
                Assert.Equal("Running", beforeCrash.State);
                Assert.True(beforeCrash.ProcessId > 0);
                var crashStarted = DateTimeOffset.UtcNow;
                var crashResult = await NativeOperationAsync("crash-test", fixture.Home, role, serviceName, MissingNativeHelperAsync);
                var pidMatch = System.Text.RegularExpressions.Regex.Match(crashResult, @"pid=(\d+)");
                Assert.True(pidMatch.Success, "Restricted helper did not return the verified fixture PID.");
                var crashedPid = int.Parse(pidMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.Equal(beforeCrash.ProcessId, crashedPid);
                output.WriteLine("SCM controlled crash requested: UTC={0:o}; service={1}; verified PID={2}; state before=Running.", crashStarted, serviceName, crashedPid);

                var recovered = await WaitForWindowsScmRecoveryAsync(serviceName, crashedPid, TimeSpan.FromSeconds(90));
                output.WriteLine("SCM recovery observed: UTC={0:o}; service={1}; previous PID={2}; new PID={3}; state={4}.", DateTimeOffset.UtcNow, serviceName, crashedPid, recovered.ProcessId, recovered.State);
                if (role == "worker") await WaitAsync(2);
                else
                {
                    await WaitAsync(1);
                    using var loginBody = new StringContent(JsonSerializer.Serialize(new { username = "deployment-operator", password = adminPassword }), System.Text.Encoding.UTF8, "application/json");
                    using var login = await fixture.Client.PostAsync("/api/identity/login", loginBody);
                    Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
                    Assert.True(File.Exists(Path.Combine(fixture.Home, "state", "platform", "identity.sqlite")));
                }
                await LivenessAsync();
                Assert.Equal("retained backup evidence", File.ReadAllText(retainedBackup));
                var crashEvents = await ReadWindowsScmCrashEventsAsync(serviceName, crashStarted.AddSeconds(-2));
                Assert.Contains("7031", crashEvents, StringComparison.Ordinal);
                output.WriteLine("SCM failure event observed: {0}", crashEvents);
            }
            await ServiceAsync("status", fixture.Home, role, serviceName);
            await ServiceAsync("restart", fixture.Home, role, serviceName); await WaitAsync(OperatingSystem.IsWindows() && role == "worker" ? 3 : 2);
            await LivenessAsync();
            var timer = Stopwatch.StartNew(); await ServiceAsync("stop", fixture.Home, role, serviceName);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(40));
            output.WriteLine("{0} {1}: native start/readiness/restart passed; graceful stop {2:F3}s.", OperatingSystem.IsWindows() ? "SCM" : "systemd", role, timer.Elapsed.TotalSeconds);
            if (OperatingSystem.IsLinux())
                Assert.DoesNotContain(canary, await NativeOperationAsync("journal", fixture.Home, role, serviceName, () => RunAsync("sudo", ["journalctl", "--unit=hispania-" + role + ".service", "--no-pager"])), StringComparison.Ordinal);
            else
                foreach (var log in Directory.EnumerateFiles(Path.Combine(fixture.Home, "logs"), "*", SearchOption.AllDirectories)) Assert.DoesNotContain(canary, File.ReadAllText(log), StringComparison.Ordinal);
            using (Hispania.Hosting.InstallationOperations.Lock(fixture.Home)) { }
            await ServiceAsync("uninstall", fixture.Home, role, serviceName); installed = false;
            if (OperatingSystem.IsWindows()) Assert.Equal("MISSING", await TryGetWindowsServiceAsync(serviceName));
            Assert.True(File.Exists(Path.Combine(fixture.Home, "config", "production.json")));
            Assert.Equal("retained backup evidence", File.ReadAllText(retainedBackup));
            if (OperatingSystem.IsLinux() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
                await NativeOperationAsync("retained-secret", fixture.Home, role, serviceName, () => RunAsync("sudo", ["test", "-f", canaryPath]));
            else Assert.True(File.Exists(canaryPath));
            if (role == "server") Assert.True(File.Exists(Path.Combine(fixture.Home, "state", "platform", "identity.sqlite")));
        }
        catch
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var log in Directory.EnumerateFiles(Path.Combine(fixture.Home, "logs"), "*", SearchOption.AllDirectories)) output.WriteLine(File.ReadAllText(log));
                var eventCommand = "Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Service Control Manager';StartTime=(Get-Date).AddMinutes(-5)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -like '*" + serviceName + "*' } | Select-Object -First 12 TimeCreated,Id,Message | Format-List | Out-String; exit 0";
                output.WriteLine(await RunAsync("pwsh", ["-NoProfile", "-Command", eventCommand]));
            }
            throw;
        }
        finally
        {
            if (installed) await ServiceAsync("uninstall", fixture.Home, role, serviceName);
            if (coordinator is not null) { await coordinator.StopAsync(); await coordinator.DisposeAsync(); }
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
            {
                await NativeOperationAsync("reclaim", fixture.Home, role, serviceName, MissingNativeHelperAsync);
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
                output.WriteLine(await NativeOperationAsync("journal", fixture.Home, role, serviceName, () => RunAsync("sudo", ["journalctl", "--unit=hispania-" + role + ".service", "--no-pager", "--lines=80"])));
            throw new InvalidOperationException("Native service did not become Ready or negotiate within its startup deadline.");
        }
        async Task LivenessAsync()
        {
            if (role != "server") return;
            using var response = await fixture.Client.GetAsync("/healthz");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
        async Task<(string State, int ProcessId)> WaitForWindowsScmRecoveryAsync(string name, int failedPid, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var current = await ReadWindowsServiceAsync(name);
                if (current.State == "Running" && current.ProcessId > 0 && current.ProcessId != failedPid) return current;
                await Task.Delay(500);
            }
            var last = await ReadWindowsServiceAsync(name);
            throw new InvalidOperationException($"SCM did not recover owned service {name}; previous PID={failedPid}; last state={last.State}; PID={last.ProcessId}.");
        }
        async Task<string> ReadWindowsScmCrashEventsAsync(string name, DateTimeOffset since)
        {
            var start = since.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            var command = "$since=[datetime]::Parse('" + start + "').ToLocalTime(); $events=@(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Service Control Manager';StartTime=$since} -ErrorAction SilentlyContinue | Where-Object { $_.Id -in @(7031,7034) -and $_.Message -like '*" + name + "*' } | Select-Object Id,@{Name='TimeUtc';Expression={$_.TimeCreated.ToUniversalTime().ToString('o')}}); $events | ConvertTo-Json -Compress";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var result = await RunAsync("pwsh", ["-NoProfile", "-Command", command]);
                if (!string.IsNullOrWhiteSpace(result) && result.Contains("7031", StringComparison.Ordinal)) return result;
                await Task.Delay(500);
            }
            return await RunAsync("pwsh", ["-NoProfile", "-Command", command]);
        }
    }

    private static async Task<(string State, int ProcessId)> ReadWindowsServiceAsync(string serviceName)
    {
        var command = "$service=Get-CimInstance Win32_Service -Filter \"Name='" + serviceName + "'\"; if($service){$service.State+'|'+$service.ProcessId}";
        var result = (await RunAsync("pwsh", ["-NoProfile", "-Command", command])).Trim();
        var values = result.Split('|');
        Assert.Equal(2, values.Length);
        return (values[0], int.Parse(values[1], CultureInfo.InvariantCulture));
    }

    private static async Task ServiceAsync(string action, string home, string role, string serviceName)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER")))
        {
            if (OperatingSystem.IsLinux() && action == "install") await RunAsync("chmod", ["-R", "a-w", Path.Combine(home, "app")]);
            await NativeOperationAsync(action, home, role, serviceName, () => throw new InvalidOperationException("CI native helper required."));
            return;
        }
        var operations = Path.Combine(ArtifactProcessTests.Artifact, "operations");
        if (OperatingSystem.IsWindows()) await RunAsync("pwsh", ["-NoProfile", "-File", Path.Combine(operations, "windows-service.ps1"), "-Action", action, "-Home", home, "-Role", role, "-ServiceName", serviceName]);
        else
        {
            if (action == "install") await RunAsync("chmod", ["-R", "a-w", Path.Combine(home, "app")]);
            await RunAsync("sudo", ["sh", Path.Combine(operations, "install-systemd.sh"), action, home, role, "hispania"]);
        }
    }

    private static Task<string> NativeOperationAsync(string action, string home, string role, string serviceName, Func<Task<string>> fallback)
    {
        var helper = Environment.GetEnvironmentVariable("HISPANIA_PHASE21_NATIVE_HELPER");
        if (string.IsNullOrEmpty(helper)) return fallback();
        return OperatingSystem.IsWindows()
            ? RunAsync("pwsh", ["-NoProfile", "-File", helper, "-Action", action, "-InstallationHome", home, "-Role", role, "-ServiceName", serviceName])
            : RunAsync("sudo", ["-n", helper, action, home, role]);
    }

    private static async Task AssertOwnedWindowsServiceAsync(string serviceName, string home, string role)
    {
        var detail = await RunAsync("pwsh", ["-NoProfile", "-Command", "Get-CimInstance Win32_Service -Filter \"Name='" + serviceName + "'\" | Select-Object Name,PathName,StartName | ConvertTo-Json -Compress"]);
        Assert.Contains(serviceName, detail, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(Path.Combine(home, "app", role == "server" ? "server/Hispania.Server.exe" : "worker/Hispania.WorkerAgent.exe")), detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--home", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.GetFullPath(home), detail, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> TryGetWindowsServiceAsync(string serviceName) =>
        (await RunAsync("pwsh", ["-NoProfile", "-Command", "if (Get-Service -Name '" + serviceName + "' -ErrorAction SilentlyContinue) { 'FOUND' } else { 'MISSING' }"])).Trim();

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
