using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hispania.Hosting;

public sealed class ProductionInstallation
{
    public string Home { get; }
    public ProductionConfiguration Configuration { get; }
    public IInstallationSecretProvider Secrets { get; }
    public bool CheckOnly { get; }
    public bool BootstrapOnly { get; }
    public string State => Path.Combine(Home, "state");
    public string PrivateFile(string relative) => Path.Combine(Home, "secrets", relative);

    public IDisposable AcquireRuntimeLease()
    {
        var lease = InstallationOperations.Lock(Home);
        if (File.Exists(Path.Combine(Home, "run", "upgrade-in-progress.json")))
        { lease.Dispose(); throw new DeploymentException(6, "Interrupted upgrade requires explicit recovery before startup."); }
        return lease;
    }

    public void RecordStartupIntent()
    {
        try
        {
            var record = Path.Combine(Home, "run", "last-upgrade.json");
            if (File.Exists(record))
            {
                var operation = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(record))!.AsObject();
                operation["newHostStarted"] = true;
                var pending = record + ".pending";
                File.WriteAllText(pending, operation.ToJsonString()); File.Move(pending, record, true);
            }
        }
        catch { throw new DeploymentException(6, "Upgrade startup record is invalid; inspect recovery evidence before starting."); }
    }

    private ProductionInstallation(string home, ProductionConfiguration configuration, bool checkOnly, bool bootstrapOnly)
    {
        Home = home;
        Configuration = configuration;
        CheckOnly = checkOnly;
        BootstrapOnly = bootstrapOnly;
        Secrets = new InstallationSecrets(Path.Combine(home, "secrets"));
    }

    public static bool IsProduction(string[] args)
    {
#if HISPANIA_PRODUCTION_DISTRIBUTION
        return true;
#else
        return args.Any(argument => argument == "--home" || argument.StartsWith("--home=", StringComparison.Ordinal)) || Environment.GetEnvironmentVariable("HISPANIA__HOME") is not null || File.Exists(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "artifact-manifest.json")));
#endif
    }

    public static ProductionInstallation? Load(string[] args, string role)
    {
        if (!IsProduction(args)) return null;
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new DeploymentException(5, "Production hosting currently supports Windows x64 and Linux x64; this platform has not been validated.");
        string? home = Environment.GetEnvironmentVariable("HISPANIA__HOME");
        var overrides = new List<KeyValuePair<string, string?>>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (!key.StartsWith("HISPANIA__", StringComparison.OrdinalIgnoreCase) || key.Equals("HISPANIA__HOME", StringComparison.OrdinalIgnoreCase)) continue;
            overrides.Add(new(key[10..], entry.Value?.ToString()));
        }
        bool check = false, rootSeen = false, bootstrap = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument == "--check") { check = true; continue; }
            if (argument == "--bootstrap") { bootstrap = true; continue; }
            if (argument == "--home" || argument.StartsWith("--home=", StringComparison.Ordinal))
            {
                ProductionConfiguration.Require(!rootSeen, "home", "only one explicit root is allowed"); rootSeen = true;
                home = argument == "--home" && index + 1 < args.Length ? args[++index] : argument.Length > 7 ? argument[7..] : null;
                continue;
            }
            ProductionConfiguration.Require(argument.StartsWith("--set=", StringComparison.Ordinal) && argument[6..].Contains('=', StringComparison.Ordinal), "arguments", "use --home ROOT, --check or --set=FIELD=VALUE");
            var setting = argument[6..]; var split = setting.IndexOf('=', StringComparison.Ordinal);
            overrides.Add(new(setting[..split], setting[(split + 1)..]));
        }
        ProductionConfiguration.Require(home is not null && Path.IsPathFullyQualified(home), "home", "provide an absolute installation root");
        home = Path.GetFullPath(home!);
        RejectLinks(home);
        if (File.Exists(Path.Combine(home, "run", "upgrade-in-progress.json"))) throw new DeploymentException(6, "Interrupted upgrade: inspect run/upgrade-in-progress.json and recover before starting.");
        var configuration = ProductionConfiguration.Read(Path.Combine(home, "config", "production.json"), overrides);
        configuration.Validate(role);
        ProductionConfiguration.Require(!bootstrap || role == "server" && !check && configuration.BootstrapSecret is not null && configuration.BootstrapPasswordSecret is not null && !string.IsNullOrWhiteSpace(configuration.BootstrapUsername), "bootstrap", "requires server, bootstrapUsername, bootstrapSecret and bootstrapPasswordSecret; cannot combine with --check");
        return new ProductionInstallation(home, configuration, check, bootstrap);
    }

    public void ValidatePrerequisites()
    {
        InstallationMetadata.Validate(Home, Configuration.Role);
        foreach (var name in new[] { "state", "logs", "backups", "run" })
        {
            var directory = Path.Combine(Home, name); RejectLinks(directory);
            if (!Directory.Exists(directory)) throw new DeploymentException(5, $"Missing writable directory: {name}; run installation provisioning.");
            var probe = Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N"));
            try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } File.Delete(probe); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new DeploymentException(5, $"Directory is not writable: {name}; grant service account access."); }
        }
        foreach (var reference in new[] { Configuration.BootstrapSecret, Configuration.BootstrapPasswordSecret, Configuration.PostgreSql.PasswordSecret, Configuration.Tls.PasswordSecret, Configuration.Worker.PasswordSecret })
            if (reference is not null) _ = Secrets.Resolve(reference);
        foreach (var path in new[] { Configuration.Tls.CertificateFile, Configuration.Worker.CertificateFile })
            if (path is not null) InstallationSecrets.ValidateProtectedFile(PrivateFile(path));
        foreach (var entry in new[] { (Role: "server-tls", Path: Configuration.Tls.CertificateFile, Password: Configuration.Tls.PasswordSecret), (Role: "worker-mtls", Path: Configuration.Worker.CertificateFile, Password: Configuration.Worker.PasswordSecret) })
        {
            if (entry.Path is null) continue;
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(PrivateFile(entry.Path), entry.Password is null ? null : Secrets.Resolve(entry.Password), System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
            var status = CertificateSecurityPolicy.Evaluate(certificate, DateTimeOffset.UtcNow);
            if (status.Status is "expired" or "not-yet-valid") throw new DeploymentException(3, "Configured TLS certificate is outside its validity period; rotate private material before startup.");
            Console.WriteLine(JsonSerializer.Serialize(new { operation = "certificate-check", role = entry.Role, status.Status, status.NotBefore, status.NotAfter, warningDays = CertificateSecurityPolicy.WarningDays }));
        }
        var moduleHost = Path.Combine(AppContext.BaseDirectory, "module-host", "Hispania.ModuleHost.dll");
        if (!File.Exists(moduleHost)) throw new DeploymentException(5, "Missing ModuleHost child artifact; reinstall a complete publication.");
        if (Configuration.Role == "server")
        {
            CheckTool(Configuration.Ffprobe, "ffprobe"); CheckTool(Configuration.Ffmpeg, "ffmpeg");
            var web = Path.Combine(AppContext.BaseDirectory, "wwwroot", Configuration.WebBuild, "index.html");
            if (!File.Exists(web)) throw new DeploymentException(5, "Missing Web build; reinstall a complete publication.");
            foreach (var root in Configuration.LibraryRoots)
                if (!Directory.Exists(root)) throw new DeploymentException(5, "Library root is unavailable; mount configured media directories before startup.");
        }
        else if (Configuration.Worker.FfmpegEnabled) CheckTool(Configuration.Ffmpeg, "ffmpeg");
        foreach (var assembly in Configuration.Worker.CapabilityProviderAssemblies)
            if (!File.Exists(Path.Combine(Home, "app", assembly))) throw new DeploymentException(5, "Missing configured Worker capability provider; use an artifact-relative assembly from the verified publication.");
    }

    private static void CheckTool(string executable, string name)
    {
        ProductionConfiguration.Require(!string.IsNullOrWhiteSpace(executable) && (Path.IsPathFullyQualified(executable) || Path.GetFileName(executable) == executable), name, "use an absolute executable path or PATH command name");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, ArgumentList = { "-version" } });
            if (process is null) throw new InvalidOperationException();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(true); throw new InvalidOperationException(); }
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { throw new DeploymentException(5, $"Missing or invalid dependency: {name}; install it or configure an absolute executable path."); }
    }

    public void Apply(IConfigurationBuilder builder)
    {
        builder.Sources.Clear();
        var config = Configuration;
        var values = new Dictionary<string, string?>
        {
            ["urls"] = config.Endpoint,
            ["Web:Build"] = config.WebBuild,
            ["Logging:LogLevel:Default"] = config.LogLevel,
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning",
            ["Backup:ManagedRoot"] = State,
            ["Modules:HostPath"] = Path.Combine(AppContext.BaseDirectory, "module-host", "Hispania.ModuleHost.dll"),
            ["Modules:ExtensionsPath"] = Path.Combine(State, "extensions"),
            ["Modules:RequirePublisherSignature"] = "true",
            ["Ffmpeg:ExecutablePath"] = config.Ffmpeg,
            ["Ffprobe:ExecutablePath"] = config.Ffprobe,
            ["Ffmpeg:WorkerAssemblyPath"] = Path.Combine(AppContext.BaseDirectory, "ffmpeg-worker", "Hispania.FFmpeg.Worker.dll"),
            ["Persistence:Provider"] = config.Persistence,
            ["Platform:Capacity:ReserveBytes"] = config.Capacity.ReserveBytes.ToString(CultureInfo.InvariantCulture),
            ["Platform:Capacity:WarningFreeFraction"] = config.Capacity.WarningFreeFraction.ToString(CultureInfo.InvariantCulture),
            ["Platform:Capacity:CriticalFreeFraction"] = config.Capacity.CriticalFreeFraction.ToString(CultureInfo.InvariantCulture),
            ["Platform:Maintenance:JobIdempotencyHorizonDays"] = config.Maintenance.JobIdempotencyHorizonDays.ToString(CultureInfo.InvariantCulture),
            ["Platform:Maintenance:OrphanGraceHours"] = config.Maintenance.OrphanGraceHours.ToString(CultureInfo.InvariantCulture),
            ["Backup:ArchiveRoot"] = config.Maintenance.BackupArchiveRoot,
            ["DistributedWorker:CoordinatorUri"] = config.Worker.CoordinatorUri,
            ["DistributedWorker:NodeId"] = config.Worker.NodeId,
            ["DistributedWorker:ClientCertificatePath"] = config.Worker.CertificateFile is null ? null : PrivateFile(config.Worker.CertificateFile),
            ["DistributedWorker:MaximumConcurrentJobs"] = config.Worker.MaximumConcurrentJobs.ToString(CultureInfo.InvariantCulture),
            ["DistributedWorker:LeaseSeconds"] = config.Worker.LeaseSeconds.ToString(CultureInfo.InvariantCulture),
            ["DistributedWorker:PollMilliseconds"] = config.Worker.PollMilliseconds.ToString(CultureInfo.InvariantCulture),
            ["DistributedWorker:HeartbeatSeconds"] = config.Worker.HeartbeatSeconds.ToString(CultureInfo.InvariantCulture)
        };
        values["DistributedWorker:Ffmpeg:Enabled"] = config.Worker.FfmpegEnabled.ToString(CultureInfo.InvariantCulture);
        values["DistributedWorker:Ffmpeg:ExecutablePath"] = config.Ffmpeg;
        values["DistributedWorker:Ffmpeg:WorkerAssemblyPath"] = Path.Combine(Home, "app", "server", "ffmpeg-worker", "Hispania.FFmpeg.Worker.dll");
        values["DistributedWorker:Ffmpeg:CachePath"] = Path.Combine(State, "cache");
        values["DistributedWorker:Ffmpeg:WorkerPolicy:MaximumConcurrentJobs"] = config.Worker.MaximumConcurrentJobs.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, reference) in new Dictionary<string, string?>
        {
            ["Identity:Bootstrap:Token"] = config.BootstrapSecret,
            ["Kestrel:Certificates:Default:Password"] = config.Tls.PasswordSecret,
            ["DistributedWorker:ClientCertificatePassword"] = config.Worker.PasswordSecret
        }) if (reference is not null) values[key] = Secrets.Resolve(reference);
        if (config.Tls.CertificateFile is not null) values["Kestrel:Certificates:Default:Path"] = PrivateFile(config.Tls.CertificateFile);
        var libraryRoots = config.LibraryRoots.Length == 0 ? [Path.Combine(State, "library")] : config.LibraryRoots;
        if (config.LibraryRoots.Length == 0) Directory.CreateDirectory(libraryRoots[0]);
        for (var i = 0; i < libraryRoots.Length; i++) { values[$"Library:Roots:{i}:Key"] = "root-" + i.ToString(CultureInfo.InvariantCulture); values[$"Library:Roots:{i}:Path"] = libraryRoots[i]; }
        AddArray(values, "Modules:TrustedExtensionIds", config.TrustedExtensionIds);
        AddArray(values, "Modules:WorkerExtensionIds", config.TrustedExtensionIds);
        AddArray(values, "Modules:RevokedPublisherIds", config.RevokedPublisherIds);
        AddArray(values, "DistributedWorker:CoordinatorCertificateSha256Pins", config.Worker.CoordinatorCertificateSha256Pins);
        AddArray(values, "DistributedWorker:ContractAssemblies", config.Worker.ContractAssemblies);
        foreach (var (key, value) in config.TrustedPublisherKeys) values["Modules:TrustedPublisherKeys:" + key] = value;
        foreach (var assembly in config.Worker.CapabilityProviderAssemblies)
            ProductionConfiguration.Require(!Path.IsPathFullyQualified(assembly) && !assembly.Split('/', '\\').Any(part => part is ".." or "."), "worker.capabilityProviderAssemblies", "use artifact-relative paths");
        AddArray(values, "DistributedWorker:CapabilityProviderAssemblies", config.Worker.CapabilityProviderAssemblies.Select(path => Path.Combine(Home, "app", path)).ToArray());
        builder.AddInMemoryCollection(values);
    }

    private static void AddArray(Dictionary<string, string?> values, string prefix, string[] entries)
    { for (var i = 0; i < entries.Length; i++) values[prefix + ":" + i.ToString(CultureInfo.InvariantCulture)] = entries[i]; }

    public void EnforcePublicTrust(ConfigurationManager configuration)
    {
        ProductionConfiguration.Require(configuration["Persistence:Provider"] == Configuration.Persistence, "persistence", "restored owner provider differs from production configuration; explicitly reprovision matching public database settings");
        var prefixes = new[] { "Modules:TrustedExtensionIds:", "Modules:TrustedPublisherKeys:", "Modules:RevokedPublisherIds:" };
        var retained = configuration.AsEnumerable().Where(entry => !prefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal) || entry.Key == prefix.TrimEnd(':'))).ToArray();
        var values = new Dictionary<string, string?> { ["Modules:RequirePublisherSignature"] = "true" };
        AddArray(values, "Modules:TrustedExtensionIds", Configuration.TrustedExtensionIds);
        AddArray(values, "Modules:RevokedPublisherIds", Configuration.RevokedPublisherIds);
        foreach (var (key, value) in Configuration.TrustedPublisherKeys) values["Modules:TrustedPublisherKeys:" + key] = value;
        // Null assignment leaves array children visible to the binder. Rebuild the
        // public trust boundary while preserving every owner path and other setting.
        configuration.Sources.Clear();
        configuration.AddInMemoryCollection(retained).AddInMemoryCollection(values);
    }

    public static void AddServiceLifetime(IServiceCollection services, string role)
    {
        services.AddWindowsService(options => options.ServiceName = role == "server" ? "HispaniaServer" : "HispaniaWorker");
        services.AddSystemd();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
    }

    public void ConfigureLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        var sensitive = new[] { Configuration.BootstrapSecret, Configuration.BootstrapPasswordSecret, Configuration.PostgreSql.PasswordSecret, Configuration.Tls.PasswordSecret, Configuration.Worker.PasswordSecret }
            .Where(reference => reference is not null).Select(reference => Secrets.Resolve(reference!));
        logging.AddProvider(new ProductionLogProvider(Path.Combine(Home, "logs", Configuration.Role + ".jsonl"), sensitive));
    }

    public static void WriteCheckResult() => Console.WriteLine(JsonSerializer.Serialize(new { status = "configuration_and_dependencies_valid", configurationGeneration = ProductionConfiguration.SchemaGeneration, backupTools = "pg_dump/pg_restore are required only for PostgreSQL backup/restore", readiness = "not_started; connectivity and owner schemas are checked during startup" }));

    public static int ReportFailure(Exception exception)
    {
        var failure = exception as DeploymentException;
        Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = failure?.ExitCode ?? 4, action = failure?.Message ?? "Startup failed; check endpoint availability, provisioned credentials, store compatibility and permissions. Private exception details are suppressed." }));
        return failure?.ExitCode ?? 4;
    }

    internal static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new DeploymentException(2, "Installation directories cannot use symbolic links or reparse points.");
    }
}
