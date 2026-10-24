using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>Owns only the new process tree created by an isolated diagnostic launch.</summary>
internal sealed class NativeDiagnosticJob : IDisposable
{
    private readonly SafeFileHandle handle;
    private bool closed;
    internal bool CleanupConfirmed { get; private set; }
    internal int? CleanupErrorCode { get; private set; }
    internal IntPtr DangerousHandle => !closed ? handle.DangerousGetHandle() : throw new ObjectDisposedException(nameof(NativeDiagnosticJob));

    internal NativeDiagnosticJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x00002000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
        }
    }

    internal void VerifyMembership(IntPtr process)
    {
        if (!IsProcessInJob(process, handle, out var member)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!member) throw new InvalidOperationException("The diagnostic process did not enter its owned Job.");
    }

    internal IReadOnlyList<int> ProcessIds
    {
        get
        {
            if (closed) return Array.Empty<int>();
            for (var capacity = 32; capacity <= 16384; capacity *= 2)
            {
                var size = checked(8 + capacity * IntPtr.Size);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!QueryInformationJobObject(handle, 3, buffer, (uint)size, out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 234) continue;
                        throw new Win32Exception(error);
                    }
                    var count = Marshal.ReadInt32(buffer, 4);
                    if (count < 0 || count > capacity) throw new IOException("Invalid diagnostic Job process count.");
                    var result = new int[count];
                    for (var i = 0; i < count; i++) result[i] = checked((int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64());
                    return Array.AsReadOnly(result);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new IOException("Diagnostic Job process count exceeded its bound.");
        }
    }

    internal bool Close()
    {
        if (closed) return CleanupConfirmed;
        try
        {
            if (ProcessIds.Count != 0)
            {
                if (!TerminateJobObject(handle, 0xeec0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(3) && ProcessIds.Count != 0) Thread.Sleep(10);
            }
            CleanupConfirmed = ProcessIds.Count == 0;
        }
        catch (Win32Exception error) { CleanupErrorCode = error.NativeErrorCode; CleanupConfirmed = false; }
        catch (Exception error) when (error is IOException or OverflowException) { CleanupConfirmed = false; }
        finally { closed = true; handle.Dispose(); }
        return CleanupConfirmed;
    }

    public void Dispose() => Close();

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
    {
        internal BasicLimitInformation Basic;
        internal IoCounters Io;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation value, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr value, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
