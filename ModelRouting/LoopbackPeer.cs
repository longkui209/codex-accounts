using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>TCP loopback is not a user boundary. Confirm the connecting process's Windows SID as well.</summary>
internal static class LoopbackPeer
{
    public static bool IsCurrentUser(int clientPort, int serverPort)
        => TryGetCurrentUserProcessId(clientPort, serverPort, out _);
    public static bool TryGetCurrentUserProcessId(int clientPort, int serverPort, out int processId)
    {
        processId = 0;
        IntPtr table = IntPtr.Zero;
        try
        {
            var size = 0;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0) != 122 || size <= 4 || size > 16 * 1024 * 1024) return false;
            table = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(table, ref size, false, 2, 5, 0) != 0) return false;
            var count = Marshal.ReadInt32(table);
            if (count < 0 || 4L + count * 24L > size) return false;
            var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
            for (var i = 0; i < count; i++)
            {
                var row = IntPtr.Add(table, 4 + i * 24);
                if (Marshal.ReadInt32(row) != 5 || unchecked((uint)Marshal.ReadInt32(row, 4)) != loopback ||
                    unchecked((uint)Marshal.ReadInt32(row, 12)) != loopback || Port(row, 8) != clientPort || Port(row, 16) != serverPort) continue;
                using var process = OpenProcess(0x1000, false, Marshal.ReadInt32(row, 20));
                if (process.IsInvalid || !OpenProcessToken(process, 8, out var token)) return false;
                using (token)
                using (var peer = new WindowsIdentity(token.DangerousGetHandle()))
                using (var current = WindowsIdentity.GetCurrent())
                {
                    if (peer.User is null || current.User is null || !peer.User.Equals(current.User)) return false;
                    processId = Marshal.ReadInt32(row, 20);
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
        finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
    }
    private static int Port(IntPtr row, int offset)
    {
        var encoded = unchecked((uint)Marshal.ReadInt32(row, offset));
        return (int)(((encoded & 255) << 8) | ((encoded >> 8) & 255));
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
