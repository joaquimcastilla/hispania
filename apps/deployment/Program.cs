using Hispania.Hosting;

try
{
    switch (args)
    {
        case ["install", var artifact, var home, var role]: InstallationOperations.Install(artifact, home, role); break;
        case ["verify", var artifact]: _ = ArtifactManifest.Verify(artifact); break;
        case ["upgrade", var artifact, var home]: Console.WriteLine(await InstallationOperations.UpgradeAsync(artifact, home)); break;
        case ["plan-migrate-complete-rc1-to-platform-only", var artifact, var home]:
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(InstallationOperations.PlanCompleteRc1ToPlatformOnly(artifact, home), new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true })); break;
        case ["migrate-complete-rc1-to-platform-only", var artifact, var home, var manifestSha256]: Console.WriteLine(await InstallationOperations.MigrateCompleteRc1ToPlatformOnlyAsync(artifact, home, manifestSha256)); break;
        case ["rollback-restore", var home, var bundle]: await InstallationOperations.RestoreRollbackAsync(home, bundle); break;
        case ["recover-before-start", var home]: InstallationOperations.RecoverBeforeStartup(home); break;
        case ["uninstall-binaries", var home]: InstallationOperations.UninstallBinaries(home); break;
        default: throw new DeploymentException(2, "Usage: install ARTIFACT HOME server|worker; verify ARTIFACT; upgrade ARTIFACT HOME; plan-migrate-complete-rc1-to-platform-only ARTIFACT HOME; migrate-complete-rc1-to-platform-only ARTIFACT HOME TARGET_MANIFEST_SHA256; rollback-restore HOME BUNDLE; recover-before-start HOME; uninstall-binaries HOME. All paths must be absolute.");
    }
}
catch (Exception exception) { Environment.ExitCode = ProductionInstallation.ReportFailure(exception); }
