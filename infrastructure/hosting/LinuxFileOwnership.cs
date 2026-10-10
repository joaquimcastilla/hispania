using System.Runtime.InteropServices;

namespace Hispania.Hosting;

// Linux x64 is the only Linux production RID in this phase. No vault/key management.
internal static partial class LinuxFileOwnership
{
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatus
    {
        [FieldOffset(28)] internal uint UserId;
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FileStat(int descriptor, out FileStatus status);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUser();

    internal static void Validate(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new DeploymentException(5, "Protected local files require supported Windows or Linux x64 hosts.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FileStat(handle.DangerousGetHandle().ToInt32(), out var status) != 0 || status.UserId != EffectiveUser())
            throw new DeploymentException(3, "Private file owner must be the effective service user; reprovision ownership before startup.");
    }
}
