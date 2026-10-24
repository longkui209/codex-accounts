using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CodexAccountSwitcher.Core;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>
/// A signed desktop can replace its initial process during normal startup or relaunch.
/// Follow only the same user's exact, already-verified installation and new startup era.
/// This observes public process metadata; it never reads arguments, attaches IPC or stops a desktop.
/// </summary>
internal sealed class NativeDesktopLifetime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnknownWindow = TimeSpan.FromSeconds(60);
    private readonly string desktopExecutable;
    private readonly string cliExecutable;
    private readonly string[] cliExecutables;
    private readonly string currentSid;
    private readonly DateTimeOffset hostStartUtc;

    public NativeDesktopLifetime(string desktopExecutable, string cliExecutable, DateTimeOffset hostStartUtc,IReadOnlyList<string>? additionalCliPaths=null)
    {
        this.desktopExecutable = ValidateExecutablePath(desktopExecutable);
        this.cliExecutable = ValidateExecutablePath(cliExecutable, "codex.exe");
        cliExecutables=new[]{this.cliExecutable}.Concat(additionalCliPaths??Array.Empty<string>()).Select(path=>ValidateExecutablePath(path,"codex.exe")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        currentSid = GetCurrentUserSid();
        this.hostStartUtc = hostStartUtc;
    }

    public Task TrackUntilClosedAsync(Process initial, TimeSpan relaunchGrace,
        Func<bool>? keepAliveForActive, CancellationToken cancellation)
        => TrackUntilClosedAsync(initial, desktopExecutable, hostStartUtc, relaunchGrace,
            keepAliveForActive, cancellation);

    /// <summary>
    /// Authorizes only the verified official CLI currently parented by this startup's native desktop.
    /// A different same-user CLI cannot establish binding or use this session's subscriptions.
    /// </summary>
    public bool IsNativeBackendCaller(int pid)
    {
        if(pid<=0)return false;
        foreach(var cli in cliExecutables)
        {
        if (!TryReadCandidate(pid, cli, out var caller, out _) || caller is null ||
            !TryReadParent(pid, out var parentPid)) continue;
        if(CallerMatches(caller, parentPid, ReadSnapshot(desktopExecutable), desktopExecutable,
            cli, currentSid, hostStartUtc, DateTimeOffset.UtcNow))return true;
        }
        return false;
    }

    public static async Task TrackUntilClosedAsync(Process initial, string canonicalExecutablePath,
        DateTimeOffset hostStartUtc, TimeSpan relaunchGrace, Func<bool>? keepAliveForActive,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _ = initial.Id; // Must be the Process returned by the caller's successful native launch.
        var executable = ValidateExecutablePath(canonicalExecutablePath);
        var currentSid = GetCurrentUserSid();
        var tracker = new Tracker(executable, currentSid, hostStartUtc, relaunchGrace);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var active = keepAliveForActive?.Invoke() == true;
            var outcome = tracker.Observe(ReadSnapshot(executable), DateTimeOffset.UtcNow, active);
            if (outcome == Outcome.Closed) return;
            if (outcome == Outcome.StartupNotConfirmed)
                throw new UserFacingException("未确认到本次启动的官方桌面。订阅连接没有成功，普通入口和登录保持原样。");
            if (outcome == Outcome.Unverified)
                throw new UserFacingException("无法可靠确认官方桌面的运行状态，订阅连接未通过核验。没有关闭桌面或修改登录。");
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
        }
    }

    private static string GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException();
        }
        catch
        {
            throw new UserFacingException("无法确认本机用户的桌面启动身份，未把订阅连接标记为成功。");
        }
    }

    private static string ValidateExecutablePath(string path, string executableName = "ChatGPT.exe")
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) ||
                !Path.GetFileName(path).Equals(executableName, StringComparison.OrdinalIgnoreCase))
                throw new UserFacingException("官方桌面生命周期路径无效。");
            return Path.GetFullPath(path);
        }
        catch (UserFacingException) { throw; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new UserFacingException("官方桌面生命周期路径无效。"); }
    }

    private static Snapshot ReadSnapshot(string executable)
    {
        var observed = new List<Candidate>();
        var unknown = false;
        Process[] processes;
        try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)); }
        catch { return new Snapshot([], true); }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (TryReadCandidate(process.Id, executable, out var candidate, out var unreadable) && candidate is not null)
                        observed.Add(candidate);
                    unknown |= unreadable;
                }
                catch (InvalidOperationException) { /* The process exited during enumeration. */ }
                catch (ArgumentException) { unknown = true; }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or
                                                     System.Security.SecurityException or IOException)
                { unknown = true; }
            }
        }
        return new Snapshot(observed, unknown);
    }

    private static bool TryReadCandidate(int pid, string executable, out Candidate? candidate, out bool unknown)
    {
        candidate = null;
        unknown = false;
        try
        {
            using var handle = OpenProcess(0x1000, false, pid);
            if (handle.IsInvalid)
            {
                // A disappearing process is a normal enumeration race. Access denial remains unknown.
                unknown = Marshal.GetLastWin32Error() is not 87 and not 6;
                return false;
            }
            var capacity = 32768;
            var image = new char[capacity];
            if (!QueryFullProcessImageName(handle, 0, image, ref capacity))
            { unknown = true; return false; }
            var path = Path.GetFullPath(new string(image, 0, capacity));
            if (!path.Equals(executable, StringComparison.OrdinalIgnoreCase)) return false;
            if (!GetProcessTimes(handle, out var created, out var exited, out _, out _))
            { unknown = true; return false; }
            if (exited != 0) return false;
            if (!OpenProcessToken(handle, 8, out var token))
            { unknown = true; return false; }
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                var sid = identity.User?.Value;
                if (sid is null) { unknown = true; return false; }
                candidate = new Candidate(pid, path, sid, DateTimeOffset.FromFileTime(created).ToUniversalTime());
                return true;
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or
                                             UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { unknown = true; return false; }
    }

    private static bool TryReadParent(int pid, out int parentPid)
    {
        parentPid = 0;
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return false;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(snapshot, ref entry)) return false;
        do
        {
            if (entry.ProcessId != pid) continue;
            if (entry.ParentProcessId > int.MaxValue) return false;
            parentPid = (int)entry.ParentProcessId;
            return parentPid > 0;
        } while (Process32Next(snapshot, ref entry));
        return false;
    }

    internal static bool CallerMatches(Candidate caller, int parentPid, Snapshot desktopSnapshot,
        string desktopExecutable, string cliExecutable, string currentSid,
        DateTimeOffset hostStartUtc, DateTimeOffset now)
    {
        if (caller.Pid <= 0 || parentPid <= 0 || caller.Pid == parentPid ||
            !caller.ImagePath.Equals(cliExecutable, StringComparison.OrdinalIgnoreCase) ||
            !caller.UserSid.Equals(currentSid, StringComparison.Ordinal) ||
            caller.CreatedUtc < hostStartUtc.AddSeconds(-1) || caller.CreatedUtc > now.AddSeconds(1)) return false;
        return desktopSnapshot.Candidates.Any(parent => parent.Pid == parentPid &&
            parent.ImagePath.Equals(desktopExecutable, StringComparison.OrdinalIgnoreCase) &&
            parent.UserSid.Equals(currentSid, StringComparison.Ordinal) &&
            parent.CreatedUtc >= hostStartUtc.AddSeconds(-1) && parent.CreatedUtc <= caller.CreatedUtc);
    }

    internal enum Outcome { KeepAlive, Closed, StartupNotConfirmed, Unverified }
    internal sealed record Candidate(int Pid, string ImagePath, string UserSid, DateTimeOffset CreatedUtc);
    internal sealed record Snapshot(IReadOnlyList<Candidate> Candidates, bool MetadataUnknown);

    /// <summary>Pure decision state, exercised without launching or reading any real process.</summary>
    internal sealed class Tracker(string canonicalExecutablePath, string currentSid,
        DateTimeOffset hostStartUtc, TimeSpan relaunchGrace)
    {
        private readonly TimeSpan grace = relaunchGrace >= TimeSpan.FromSeconds(3)
            ? relaunchGrace : TimeSpan.FromSeconds(3);
        private bool seenDesktop;
        private DateTimeOffset? emptySince;
        private DateTimeOffset? unknownSince;

        public Outcome Observe(Snapshot snapshot, DateTimeOffset now, bool active)
        {
            var matching = snapshot.Candidates.Any(candidate => candidate.Pid > 0 &&
                candidate.ImagePath.Equals(canonicalExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                candidate.UserSid.Equals(currentSid, StringComparison.Ordinal) &&
                candidate.CreatedUtc >= hostStartUtc.AddSeconds(-1) && candidate.CreatedUtc <= now.AddSeconds(1));
            if (matching)
            {
                seenDesktop = true;
                emptySince = null;
                unknownSince = null;
                return Outcome.KeepAlive;
            }
            if (active)
            {
                // Never dispose a relay beneath an already authenticated in-flight request.
                emptySince = null;
                unknownSince = null;
                return Outcome.KeepAlive;
            }
            if (snapshot.MetadataUnknown)
            {
                emptySince = null;
                unknownSince ??= now;
                return now - unknownSince.Value >= UnknownWindow ? Outcome.Unverified : Outcome.KeepAlive;
            }
            unknownSince = null;
            if (!seenDesktop)
                return now - hostStartUtc < StartupWindow ? Outcome.KeepAlive : Outcome.StartupNotConfirmed;
            emptySince ??= now;
            return now - emptySince.Value >= grace ? Outcome.Closed : Outcome.KeepAlive;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        [Out] char[] executableName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited,
        out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess,
        out SafeAccessTokenHandle token);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
