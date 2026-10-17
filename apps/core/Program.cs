using Hispania.Core.Configuration;
using Hispania.Core.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Hispania.Core;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
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
}
