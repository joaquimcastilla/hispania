namespace Hispania.Compatibility.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--phase16-", StringComparison.Ordinal)) return await Phase16HistoricalDataUpgradeTests.RunChildAsync(args);
        if (args.Length != 2 || args[0] != "--historical-package") return 2;
        try { await Phase15CompatibilityTests.ExerciseHistoricalPackageAsync(args[1]); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
