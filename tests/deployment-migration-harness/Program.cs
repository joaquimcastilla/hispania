using Hispania.Hosting;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: artifact HOME TARGET_MANIFEST_SHA256 FAIL_AT_CHECKPOINT");
    return 2;
}

var (artifact, home, targetManifestSha256, failAt) = (args[0], args[1], args[2], args[3]);
await InstallationOperations.MigrateCompleteRc1ToPlatformOnlyWithCheckpointsAsync(
    artifact,
    home,
    targetManifestSha256,
    checkpoint =>
    {
        if (checkpoint == failAt)
            Environment.FailFast("Controlled test-only process termination at migration checkpoint: " + failAt);
    });
return 0;
