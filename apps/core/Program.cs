using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Hispania.Core.Identity;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hispania.Core;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 0)
            {
                if (args.Length == 2 && string.Equals(args[0], "backup-core-state", StringComparison.Ordinal))
                    return await BackupCoreStateAsync(args[1]).ConfigureAwait(false);
                if (args.Length == 2 && string.Equals(args[0], "restore-core-state", StringComparison.Ordinal))
                    return await RestoreCoreStateAsync(args[1]).ConfigureAwait(false);
                if (args.Length != 1 || !string.Equals(args[0], "bootstrap-admin", StringComparison.Ordinal) &&
                    !string.Equals(args[0], "invalidate-sessions", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Usage: Hispania.Core [bootstrap-admin|invalidate-sessions|backup-core-state <path>|restore-core-state <path>]");
                    return 2;
                }
                return string.Equals(args[0], "bootstrap-admin", StringComparison.Ordinal)
                    ? await BootstrapAdministratorAsync().ConfigureAwait(false)
                    : await InvalidateSessionsAsync().ConfigureAwait(false);
            }

            await using var app = CoreHost.Build();
            await app.RunAsync();
            return 0;
        }
        catch (CoreConfigurationException exception)
        {
            Console.Error.WriteLine($"Core configuration error: {exception.Message}");
            return 2;
        }
        catch (CoreStateBackupException exception)
        {
            Console.Error.WriteLine($"Core state operation failed: {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Core host failed during startup or shutdown ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> BootstrapAdministratorAsync()
    {
        await using var app = CoreHost.Build(httpEnabled: false);
        using var lease = CoreDataDirectoryLease.Acquire(app.Services.GetRequiredService<CoreStorageOptions>().DataDirectory, exclusive: false);
        var persistence = app.Services.GetRequiredService<ICorePersistence>();
        await persistence.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var bootstrap = app.Services.GetRequiredService<ICoreBootstrapService>();
        if (!await bootstrap.IsPendingAsync(CancellationToken.None).ConfigureAwait(false))
        {
            Console.Error.WriteLine("Initial administrator bootstrap is unavailable or already completed.");
            return 3;
        }

        var username = ReadSecretInput("Administrator username: ", echo: true);
        var password = ReadSecretInput("Password (minimum 15 characters): ", echo: false);
        var confirmation = ReadSecretInput("Confirm password: ", echo: false);
        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Passwords do not match.");
            return 2;
        }

        var result = await bootstrap.CreateInitialAdministratorAsync(username, password, CancellationToken.None).ConfigureAwait(false);
        return result switch
        {
            CoreBootstrapResult.Created => WriteBootstrapSuccess(),
            CoreBootstrapResult.InvalidInput => WriteBootstrapInvalidInput(),
            _ => WriteBootstrapFailure()
        };
    }

    private static async Task<int> InvalidateSessionsAsync()
    {
        await using var app = CoreHost.Build(httpEnabled: false);
        using var lease = CoreDataDirectoryLease.Acquire(app.Services.GetRequiredService<CoreStorageOptions>().DataDirectory, exclusive: false);
        var persistence = app.Services.GetRequiredService<ICorePersistence>();
        await persistence.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();
        var count = await sessions.InvalidateAllForLocalRecoveryAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Local recovery complete. Sessions invalidated: {count}.");
        return 0;
    }

    private static async Task<int> BackupCoreStateAsync(string path)
    {
        var options = ReadStorageOptions();
        var result = await CoreStateBackupService.CreateAsync(options, path, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Core state backup created. SHA-256: {result.Sha256}; bytes: {result.Length}.");
        return 0;
    }

    private static async Task<int> RestoreCoreStateAsync(string path)
    {
        var options = ReadStorageOptions();
        await CoreStateBackupService.RestoreAsync(options, path, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine("Core state restore completed and passed SQLite integrity verification.");
        return 0;
    }

    private static CoreStorageOptions ReadStorageOptions()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix: "HISPANIA_CORE_").Build();
        return CoreStorageOptions.Read(configuration, AppContext.BaseDirectory);
    }

    private static string ReadSecretInput(string prompt, bool echo)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
        if (echo) return Console.ReadLine() ?? string.Empty;
        var characters = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Length > 0) characters.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) characters.Append(key.KeyChar);
        }
        return characters.ToString();
    }

    private static int WriteBootstrapSuccess() { Console.WriteLine("Initial administrator created."); return 0; }
    private static int WriteBootstrapInvalidInput() { Console.Error.WriteLine("Bootstrap input does not meet the identity policy."); return 2; }
    private static int WriteBootstrapFailure() { Console.Error.WriteLine("Initial administrator bootstrap failed or was already completed."); return 3; }
}
