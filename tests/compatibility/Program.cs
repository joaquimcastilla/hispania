namespace Hispania.Compatibility.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--historical-package") return 2;
        try { await Phase15CompatibilityTests.ExerciseHistoricalPackageAsync(args[1]); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
