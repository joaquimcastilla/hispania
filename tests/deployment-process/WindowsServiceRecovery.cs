using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

namespace Hispania.Deployment.ProcessTests;

[SupportedOSPlatform("windows")]
internal static class WindowsServiceRecovery
{
    // Read the real SCM configuration; sc.exe renders action names in the OS language.
    internal static void AssertConfigured(string serviceName)
    {
        var manager = OpenSCManager(null, null, 1);
        Assert.NotEqual(IntPtr.Zero, manager);
        try
        {
            var service = OpenService(manager, serviceName, 1);
            Assert.NotEqual(IntPtr.Zero, service);
            try
            {
                Assert.False(QueryServiceConfig2(service, 2, IntPtr.Zero, 0, out var needed));
                Assert.Equal(122, Marshal.GetLastPInvokeError()); // ERROR_INSUFFICIENT_BUFFER
                Assert.InRange(needed, 1u, 65536u);
                var buffer = Marshal.AllocHGlobal(checked((int)needed));
                try
                {
                    Assert.True(QueryServiceConfig2(service, 2, buffer, needed, out _));
                    var settings = Marshal.PtrToStructure<FailureActions>(buffer);
                    Assert.Equal(86400u, settings.ResetPeriod);
                    Assert.Equal(3u, settings.Count);
                    var expected = new[] { new RecoveryAction(1, 10000), new RecoveryAction(1, 30000), new RecoveryAction(0, 0) };
                    for (var i = 0; i < expected.Length; i++)
                    {
                        var actual = Marshal.PtrToStructure<ActionData>(IntPtr.Add(settings.Actions, i * Marshal.SizeOf<ActionData>()));
                        Assert.Equal(expected[i].Type, actual.Type);
                        Assert.Equal(expected[i].Delay, actual.Delay);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Assert.True(CloseServiceHandle(service)); }
        }
        finally { Assert.True(CloseServiceHandle(manager)); }
    }

    private sealed record RecoveryAction(uint Type, uint Delay);
    [StructLayout(LayoutKind.Sequential)]
    private struct FailureActions { public uint ResetPeriod; public IntPtr RebootMessage; public IntPtr Command; public uint Count; public IntPtr Actions; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActionData { public uint Type; public uint Delay; }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
