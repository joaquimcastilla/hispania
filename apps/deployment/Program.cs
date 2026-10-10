using Hispania.Hosting;

try
{
    switch (args)
    {
        case ["install", var artifact, var home, var role]: InstallationOperations.Install(artifact, home, role); break;
        case ["verify", var artifact]: _ = ArtifactManifest.Verify(artifact); break;
        case ["upgrade", var artifact, var home]: Console.WriteLine(await InstallationOperations.UpgradeAsync(artifact, home)); break;
        case ["rollback-restore", var home, var bundle]: await InstallationOperations.RestoreRollbackAsync(home, bundle); break;
        case ["recover-before-start", var home]: InstallationOperations.RecoverBeforeStartup(home); break;
        case ["uninstall-binaries", var home]: InstallationOperations.UninstallBinaries(home); break;
        default: throw new DeploymentException(2, "Usage: install ARTIFACT HOME server|worker; verify ARTIFACT; upgrade ARTIFACT HOME; rollback-restore HOME BUNDLE; recover-before-start HOME; uninstall-binaries HOME. All paths must be absolute.");
    }
}
catch (Exception exception) { Environment.ExitCode = ProductionInstallation.ReportFailure(exception); }
