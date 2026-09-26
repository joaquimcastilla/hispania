using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hispania.Runtime;

/// <summary>Operational resource ceilings. These contain faults but do not sandbox module code.</summary>
public sealed record OutOfProcessResourceLimits
{
    // CoreCLR reserves virtual address space at startup; 512 MiB can prevent a
    // constrained .NET worker from connecting even when its committed heap is small.
    public long MaximumMemoryBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumCpuSeconds { get; init; } = 120;
    public int MaximumConcurrentRequests { get; init; } = 1;
    public int MaximumWindowsProcessCount { get; init; } = 16;
    public TimeSpan MaximumRequestDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumCpuRatePercent { get; init; } = 75;
    public bool RequireOperatingSystemLimits { get; init; } = true;

    public void Validate()
    {
        if (MaximumMemoryBytes is < (64L * 1024 * 1024) or > (64L * 1024 * 1024 * 1024)) throw new ArgumentOutOfRangeException(nameof(MaximumMemoryBytes));
        if (MaximumCpuSeconds is < 1 or > 86_400) throw new ArgumentOutOfRangeException(nameof(MaximumCpuSeconds));
        if (MaximumConcurrentRequests is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentRequests));
        if (MaximumWindowsProcessCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaximumWindowsProcessCount));
        if (MaximumRequestDuration <= TimeSpan.Zero || MaximumRequestDuration > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(MaximumRequestDuration));
        if (MaximumCpuRatePercent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumCpuRatePercent));
    }

    internal void Configure(ProcessStartInfo start)
    {
        Validate();
        if (OperatingSystem.IsLinux())
        {
            var executable = start.FileName;
            var arguments = start.ArgumentList.ToArray();
            start.FileName = "prlimit";
            start.ArgumentList.Clear();
            start.ArgumentList.Add($"--as={MaximumMemoryBytes}");
            start.ArgumentList.Add($"--cpu={MaximumCpuSeconds}");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        }
        else if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("External module resource limits support Windows and Linux.");
        start.Environment["DOTNET_GCHeapHardLimit"] = MaximumMemoryBytes.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal IDisposable? ApplyTo(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return WindowsJob.Attach(process, this); }
        catch (System.ComponentModel.Win32Exception) when (!RequireOperatingSystemLimits) { return null; }
    }

    private sealed class WindowsJob : IDisposable
    {
        private const uint ExtendedLimitInformation = 9, CpuRateControlInformation = 15;
        private const uint LimitKillOnJobClose = 0x00002000, LimitProcessMemory = 0x00000100, LimitJobMemory = 0x00000200, LimitJobTime = 0x00000004, LimitActiveProcess = 0x00000008;
        private const uint CpuRateEnable = 0x1, CpuRateHardCap = 0x4;
        private readonly SafeFileHandle handle;
        private WindowsJob(SafeFileHandle handle) => this.handle = handle;

        public static WindowsJob Attach(Process process, OutOfProcessResourceLimits policy)
        {
            var raw = CreateJobObject(IntPtr.Zero, null);
            if (raw == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var job = new WindowsJob(new SafeFileHandle(raw, ownsHandle: true));
            try
            {
                SetInfo(job.handle, ExtendedLimitInformation, new ExtendedLimits
                {
                    Basic = new BasicLimits { LimitFlags = LimitKillOnJobClose | LimitProcessMemory | LimitJobMemory | LimitJobTime | LimitActiveProcess, PerJobUserTimeLimit = policy.MaximumCpuSeconds * 10_000_000L, ActiveProcessLimit = checked((uint)policy.MaximumWindowsProcessCount) },
                    ProcessMemoryLimit = UIntPtr.CreateChecked((ulong)policy.MaximumMemoryBytes),
                    JobMemoryLimit = UIntPtr.CreateChecked((ulong)policy.MaximumMemoryBytes)
                });
                SetInfo(job.handle, CpuRateControlInformation, new CpuRate { Flags = CpuRateEnable | CpuRateHardCap, Rate = checked((uint)(policy.MaximumCpuRatePercent * 100)) });
                if (!AssignProcessToJobObject(job.handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return job;
            }
            catch { job.Dispose(); throw; }
        }

        private static void SetInfo<T>(SafeFileHandle handle, uint infoClass, T value) where T : struct
        {
            var size = Marshal.SizeOf<T>(); var pointer = Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(value, pointer, false); if (!SetInformationJobObject(handle, infoClass, pointer, (uint)size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle handle, uint infoClass, IntPtr value, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [StructLayout(LayoutKind.Sequential)] private struct CpuRate { public uint Flags, Rate; }
    }
}
