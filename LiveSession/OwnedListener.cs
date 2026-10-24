using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace CodexAccountSwitcher.LiveSession;

internal static class OwnedListener
{
    public static bool IsOwned(Process child, int port)
    {
        IntPtr table = IntPtr.Zero;
        try
        {
            if (child.HasExited) return false;
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
            if (result != 122 || size <= 4 || size > 16 * 1024 * 1024) return false;
            table = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(table, ref size, false, 2, 3, 0) != 0) return false;
            var count = Marshal.ReadInt32(table);
            const int rowBytes = 24;
            if (count < 0 || 4L + (long)count * rowBytes > size) return false;
            var local = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes());
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, 4 + index * rowBytes);
                var state = Marshal.ReadInt32(row);
                var address = unchecked((uint)Marshal.ReadInt32(row, 4));
                var encoded = unchecked((uint)Marshal.ReadInt32(row, 8));
                var decodedPort = (int)(((encoded & 255) << 8) | ((encoded >> 8) & 255));
                if (state == 2 && address == local && decodedPort == port &&
                    Marshal.ReadInt32(row, 20) == child.Id && !child.HasExited) return true;
            }
            return false;
        }
        catch { return false; }
        finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
    }
    [DllImport("iphlpapi.dll", SetLastError=true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
