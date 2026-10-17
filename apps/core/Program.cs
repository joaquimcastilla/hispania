using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Hispania.Core.Identity;
using Hispania.Core.Persistence;
using Microsoft.AspNetCore.Builder;
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
                if (args.Length != 1 || !string.Equals(args[0], "bootstrap-admin", StringComparison.Ordinal) &&
                    !string.Equals(args[0], "invalidate-sessions", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("Usage: Hispania.Core [bootstrap-admin|invalidate-sessions]");
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
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Core host failed during startup or shutdown ({exception.GetType().Name}).");
            return 1;
        }
    }

    private static async Task<int> BootstrapAdministratorAsync()
    {
        await using var app = CoreHost.Build();
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
        await using var app = CoreHost.Build();
        var persistence = app.Services.GetRequiredService<ICorePersistence>();
        await persistence.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var sessions = app.Services.GetRequiredService<ICoreSessionService>();
        var count = await sessions.InvalidateAllForLocalRecoveryAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"Local recovery complete. Sessions invalidated: {count}.");
        return 0;
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
