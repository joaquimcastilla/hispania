using System.Runtime.InteropServices;

namespace Hispania.Hosting;

// Linux file ownership layouts are ABI-specific. No vault/key management.
internal static partial class LinuxFileOwnership
{
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct FileStatus
    {
        [FieldOffset(28)] internal uint UserId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct FileStatusArm64
    {
        [FieldOffset(24)] internal uint UserId;
    }

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FileStat(int descriptor, out FileStatus status);
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FileStatArm64(int descriptor, out FileStatusArm64 status);
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint EffectiveUser();

    internal static void Validate(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new DeploymentException(5, "Protected local files require supported Windows x64, Linux x64 or Linux ARM64 hosts.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var descriptor = handle.DangerousGetHandle().ToInt32();
        var owner = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? FileStatArm64(descriptor, out var arm64Status) == 0 ? arm64Status.UserId : uint.MaxValue
            : FileStat(descriptor, out var status) == 0 ? status.UserId : uint.MaxValue;
        if (owner != EffectiveUser())
            throw new DeploymentException(3, "Private file owner must be the effective service user; reprovision ownership before startup.");
    }
}
