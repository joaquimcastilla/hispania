using Hispania.Runtime;

if (args.Length != 2) return 2;
var installer = new ExtensionPackageInstaller(args[1], new Version(1, 0, 0), []);
try
{
    using var archive = File.OpenRead(args[0]);
    await installer.InstallAsync(archive);
    Console.Error.WriteLine("Historical host incorrectly accepted a new package."); return 3;
}
catch (InvalidDataException exception) when (exception.Message.Contains("Unsupported extension package format version 2", StringComparison.Ordinal))
{
    if (Directory.Exists(args[1])) return 4;
    Console.WriteLine("Historical Phase 18 host rejected format 2 before installation/execution."); return 0;
}
