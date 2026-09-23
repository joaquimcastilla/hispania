using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text;

namespace Hispania.DevLauncher;

public sealed record LauncherSettings(string? RepositoryRoot, string? MoviesPath, string? FfprobePath, string DatabasePath)
{
    public static LauncherSettings Default => new(null, null, null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "visual-test.sqlite"));
}

public static class ProjectLocator
{
    public static string? FindRoot(string? startDirectory)
    {
        var current = string.IsNullOrWhiteSpace(startDirectory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(startDirectory);
        while (true)
        {
            if (File.Exists(Path.Combine(current, "Hispania.sln")) && Directory.Exists(Path.Combine(current, "apps", "server")) && Directory.Exists(Path.Combine(current, "apps", "web"))) return current;
            var parent = Directory.GetParent(current);
            if (parent is null) return null;
            current = parent.FullName;
        }
    }
}

public static class LauncherConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string PathForConfig => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hispania", "dev-launcher.json");
    public static LauncherSettings Load() { try { return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(PathForConfig)) ?? LauncherSettings.Default; } catch { return LauncherSettings.Default; } }
    public static void Save(LauncherSettings settings) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathForConfig)!); File.WriteAllText(PathForConfig, JsonSerializer.Serialize(settings, JsonOptions)); }
}

/// <summary>Reads the operator-managed TMDB token from a plain UTF-8 text file beside launcher source.</summary>
public static class LauncherTokenFile
{
    public const string FileName = "tmdb-token.txt";
    public static string PathFor(string repositoryRoot) => Path.Combine(Path.GetFullPath(repositoryRoot), "tools", "dev-launcher", FileName);

    public static string? Load(string repositoryRoot)
    {
        var path = PathFor(repositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) File.WriteAllText(path, string.Empty, new System.Text.UTF8Encoding(false));
        return File.ReadAllText(path).Trim().TrimStart('\uFEFF') is { Length: > 0 } value ? value : null;
    }
}

public sealed record DependencyCheck(bool IsValid, string Message);
public static class DependencyChecker
{
    public static DependencyCheck Validate(string root, string? dotnet = null, string? npm = null)
    {
        if (ProjectLocator.FindRoot(root) is null) return new(false, "La carpeta no contiene Hispania.sln, apps/server y apps/web.");
        if (string.IsNullOrWhiteSpace(dotnet) && ResolveCommand("dotnet") is null) return new(false, "No se encontró dotnet en PATH.");
        if (string.IsNullOrWhiteSpace(npm) && ResolveCommand("npm") is null) return new(false, "No se encontró npm/npm.cmd. Instala Node.js LTS y reinicia HISPANIA Dev Launcher para actualizar PATH.");
        return new(true, "Dependencias y proyecto disponibles.");
    }
    public static string? ResolveCommand(string command)
    {
        string[] names = OperatingSystem.IsWindows() && command.Equals("npm", StringComparison.OrdinalIgnoreCase) ? ["npm.cmd", "npm.exe", "npm"] : [command];
        foreach (var name in names)
        {
            try { using var p = Process.Start(new ProcessStartInfo("where.exe", name) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }); if (p is null) continue; var output = p.StandardOutput.ReadLine(); p.WaitForExit(3000); if (p.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) return output.Trim(); } catch { }
        }
        return null;
    }
}

public sealed class OwnedProcess : IDisposable
{
    private int stopStarted;
    private int disposed;
    public Process Process { get; }
    public event Action<string>? Output;
    public OwnedProcess(Process process) { Process = process; Process.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); }; Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data); }; }
    public void StartReading() { Process.BeginOutputReadLine(); Process.BeginErrorReadLine(); }
    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0) return true;
            try { return Process.HasExited; }
            catch (ObjectDisposedException) { return true; }
            catch (InvalidOperationException) { return true; }
        }
    }
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopStarted, 1) != 0 || HasExited) return;
        try { Process.CloseMainWindow(); } catch (ObjectDisposedException) { return; } catch (InvalidOperationException) { return; }
        try { await Task.WhenAny(Process.WaitForExitAsync(), Task.Delay(3000)); } catch (ObjectDisposedException) { return; } catch (InvalidOperationException) { return; }
        if (!HasExited) try { Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Process.Dispose();
    }
}

public sealed class LauncherRuntime : IAsyncDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly List<OwnedProcess> processes = [];
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    public event Action<string, string>? Log;
    public event Action<string, bool>? ReadyChanged;
    public IReadOnlyList<OwnedProcess> Processes => processes;
    public async Task StartAsync(string root, LauncherSettings settings, CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (processes.Count > 0) throw new InvalidOperationException("HISPANIA ya está iniciado.");
            if (ProjectLocator.FindRoot(root) is null) throw new InvalidOperationException("La carpeta no contiene Hispania.sln, apps/server y apps/web.");
            if (DependencyChecker.Validate(root) is { IsValid: false } check) throw new InvalidOperationException(check.Message);
            EnsurePortFree(5070); EnsurePortFree(5173);
            Directory.CreateDirectory(Path.Combine(root, "apps", "server", "wwwroot"));
            var token = LauncherTokenFile.Load(root);
            var backendEnv = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["Persistence__Sqlite__Path"] = settings.DatabasePath, ["Library__Roots__0__Key"] = "movies", ["Library__Roots__0__Path"] = settings.MoviesPath, ["Ffprobe__ExecutablePath"] = settings.FfprobePath, ["Metadata__Tmdb__ReadAccessToken"] = token, ["Metadata__Tmdb__Enabled"] = string.IsNullOrWhiteSpace(token) ? "false" : "true", ["Metadata__InventoryPath"] = string.IsNullOrWhiteSpace(settings.MoviesPath) ? null : Path.Combine(settings.MoviesPath, ".hispania-metadata-index.json") };
            processes.Add(Start(root, "dotnet", "run --project apps/server --urls http://127.0.0.1:5070", backendEnv, "backend"));
            var npm = DependencyChecker.ResolveCommand("npm") ?? throw new InvalidOperationException("No se encontró npm/npm.cmd en PATH.");
            processes.Add(Start(Path.Combine(root, "apps", "web"), npm, "run dev -- --host 127.0.0.1 --port 5173", new Dictionary<string, string?>(), "frontend"));
            await WaitReadyAsync("backend", new Uri("http://127.0.0.1:5070/healthz"), cancellationToken);
            await WaitReadyAsync("frontend", new Uri("http://127.0.0.1:5173/"), cancellationToken);
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { lifecycleGate.Release(); }
    }
    public async Task StopAsync()
    {
        await lifecycleGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycleGate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        foreach (var process in processes.AsEnumerable().Reverse()) await process.StopAsync();
        foreach (var process in processes) process.Dispose();
        processes.Clear();
        ReadyChanged?.Invoke("backend", false);
        ReadyChanged?.Invoke("frontend", false);
    }
    private OwnedProcess Start(string cwd, string file, string args, IDictionary<string, string?> env, string name)
    {
        var info = new ProcessStartInfo(file, args) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var pair in env) if (pair.Value is not null) info.Environment[pair.Key] = pair.Value;
        var process = new OwnedProcess(Process.Start(info) ?? throw new InvalidOperationException($"No se pudo iniciar {name}."));
        process.Output += line => Log?.Invoke(name, Redact(line)); process.StartReading(); return process;
    }
    private async Task WaitReadyAsync(string name, Uri uri, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { using var response = await http.GetAsync(uri, cancellationToken); if (response.IsSuccessStatusCode) { ReadyChanged?.Invoke(name, true); return; } } catch { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException($"{name} no alcanzó readiness en 45 segundos.");
    }
    private static void EnsurePortFree(int port) { if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port)) throw new InvalidOperationException($"El puerto {port} ya está ocupado por otro proceso."); }
    private static string Redact(string line) => line.Replace("Metadata__Tmdb__ReadAccessToken", "Metadata__Tmdb__ReadAccessToken", StringComparison.OrdinalIgnoreCase);
    public async ValueTask DisposeAsync() { await StopAsync(); lifecycleGate.Dispose(); http.Dispose(); }
}
