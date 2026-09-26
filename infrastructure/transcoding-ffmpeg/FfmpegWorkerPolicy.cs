using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hispania.Transcoding.FFmpeg;

/// <summary>Operational limits for process-per-job transcoding. Values are ceilings, not security guarantees.</summary>
public sealed record FfmpegWorkerPolicy
{
    public int MaximumConcurrentJobs { get; init; } = 2;
    public int MaximumWindowsProcessCountPerJob { get; init; } = 4;
    public TimeSpan MaximumJobDuration { get; init; } = TimeSpan.FromHours(4);
    public long MaximumCacheBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public long MaximumTemporaryStorageBytes { get; init; } = 40L * 1024 * 1024 * 1024;
    public long MaximumMemoryBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public int MaximumCpuSeconds { get; init; } = 4 * 60 * 60;
    public int MaximumCpuRatePercent { get; init; } = 75;

    public void Validate()
    {
        if (MaximumConcurrentJobs is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentJobs));
        if (MaximumWindowsProcessCountPerJob is < 2 or > 64) throw new ArgumentOutOfRangeException(nameof(MaximumWindowsProcessCountPerJob));
        if (MaximumJobDuration <= TimeSpan.Zero || MaximumJobDuration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(MaximumJobDuration));
        if (MaximumCacheBytes <= 0 || MaximumTemporaryStorageBytes < MaximumConcurrentJobs || MaximumTemporaryStorageBytes / MaximumConcurrentJobs > MaximumCacheBytes || MaximumMemoryBytes <= 0 || MaximumCpuSeconds <= 0 || MaximumCpuRatePercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MaximumTemporaryStorageBytes), "Resource ceilings must be positive and temporary storage must allow at least one byte per concurrent job.");
    }

    internal long MaximumOutputBytesPerJob => MaximumTemporaryStorageBytes / MaximumConcurrentJobs;

    internal void ConfigureStartInfo(ProcessStartInfo start)
    {
        if (OperatingSystem.IsLinux())
        {
            var dotnet = start.FileName;
            var arguments = start.ArgumentList.ToArray();
            start.ArgumentList.Clear();
            start.FileName = "prlimit";
            start.ArgumentList.Add($"--as={MaximumMemoryBytes}");
            start.ArgumentList.Add($"--cpu={MaximumCpuSeconds}");
            start.ArgumentList.Add($"--fsize={MaximumOutputBytesPerJob}");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(dotnet);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_GCHeapHardLimit"] = MaximumMemoryBytes.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("FFmpeg worker resource limits currently support Windows and Linux.");
        }
    }

    internal static IDisposable? AttachWindowsJob(Process process, FfmpegWorkerPolicy policy)
    {
        if (!OperatingSystem.IsWindows()) return null;
        return WindowsWorkerJob.Attach(process, policy);
    }

    private sealed class WindowsWorkerJob : IDisposable
    {
        private const uint JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectCpuRateControlInformation = 15;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const uint JobObjectLimitProcessMemory = 0x00000100;
        private const uint JobObjectLimitJobMemory = 0x00000200;
        private const uint CpuRateControlEnable = 0x1;
        private const uint CpuRateControlHardCap = 0x4;
        private readonly SafeFileHandle handle;

        private WindowsWorkerJob(SafeFileHandle handle) => this.handle = handle;

        public static WindowsWorkerJob Attach(Process process, FfmpegWorkerPolicy policy)
        {
            var raw = CreateJobObject(IntPtr.Zero, null);
            if (raw == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var handle = new SafeFileHandle(raw, ownsHandle: true);
            var job = new WindowsWorkerJob(handle);
            try
            {
                var limits = new ExtendedLimitInformation
                {
                    BasicLimitInformation = new BasicLimitInformation
                    {
                        PerJobUserTimeLimit = checked(policy.MaximumCpuSeconds * 10_000_000L),
                        LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitProcessMemory | JobObjectLimitJobMemory | 0x00000004 | 0x00000008,
                        ActiveProcessLimit = checked((uint)policy.MaximumWindowsProcessCountPerJob)
                    },
                    ProcessMemoryLimit = (UIntPtr)policy.MaximumMemoryBytes,
                    JobMemoryLimit = (UIntPtr)policy.MaximumMemoryBytes
                };
                SetInfo(handle, JobObjectExtendedLimitInformation, limits);
                var cpuRate = new CpuRateControlInformation
                {
                    ControlFlags = CpuRateControlEnable | CpuRateControlHardCap,
                    CpuRate = checked((uint)(policy.MaximumCpuRatePercent * 100))
                };
                SetInfo(handle, JobObjectCpuRateControlInformation, cpuRate);
                if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return job;
            }
            catch { job.Dispose(); throw; }
        }

        private static void SetInfo<T>(SafeFileHandle handle, uint infoClass, T value) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, pointer, false);
                if (!SetInformationJobObject(handle, infoClass, pointer, (uint)size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        public void Dispose() => handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, uint infoClass, IntPtr jobObjectInfo, uint jobObjectInfoLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuRateControlInformation { public uint ControlFlags; public uint CpuRate; }
    }
}
