using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.ModelRouting;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>Reads non-personal metadata only for the intersection of CDP processes and this launch's owned Job.</summary>
internal sealed class NativeDiagnosticProcessObservation
{
    internal sealed record Sample(int ProcessId, string Role, string ImageClassification, DateTime? CreatedAtUtc,
        bool? CreatedDuringRun, uint? SignatureFlags, bool? IsAppContainer, uint? IntegrityRid,
        bool MetadataComplete, int? Win32Error, string? FailureStage)
    { public DateTime ObservedAtUtc {get;init;} }

    private sealed class ProtectionState
    {
        internal bool SeenFive;
        internal bool Failed;
        internal int StableSamples;
        internal DateTime? FirstStableFive;
    }
    private static readonly TimeSpan StartupProtectionGrace=TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableProtectionInterval=TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<string, Sample> samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string,ProtectionState> protection=new(StringComparer.Ordinal);
    private readonly List<Sample> transitions=new();
    private readonly HashSet<string> active=new(StringComparer.Ordinal);
    private bool identityMismatch;
    private readonly NativeDiagnosticChildImage image;
    private readonly NativeDiagnosticChildImage? mainAlias;
    private readonly DateTime launchStartedUtc;
    private int captures;
    internal NativeDiagnosticProcessObservation(NativeDiagnosticChildImage childImage, DateTime startedUtc,
        NativeDiagnosticChildImage? validatedMainAlias=null)
    {
        ArgumentNullException.ThrowIfNull(childImage);
        if(validatedMainAlias is not null&&(!(validatedMainAlias.UsesLinkedChildImage||validatedMainAlias.UsesCopiedChildImage)||
            !SamePath(validatedMainAlias.SourceExecutable,childImage.SourceExecutable)||
            !SamePath(validatedMainAlias.IsolatedRoot,childImage.IsolatedRoot)))
            throw new IOException("Diagnostic main alias does not match the validated source and isolation boundary.");
        image=childImage;mainAlias=validatedMainAlias;launchStartedUtc=startedUtc;
    }
    internal IReadOnlyList<Sample> Samples => samples.Values.OrderBy(row => row.ProcessId).ThenBy(row => row.CreatedAtUtc).ToArray();
    internal IReadOnlyList<Sample> TransitionSamples=>transitions.ToArray();
    internal int Captures => captures;
    internal string Mode => image.Mode;
    internal bool MainLaunchUsesAlias=>mainAlias is not null;
    internal bool BrowserVerified => samples.Any(pair => active.Contains(pair.Key)&&pair.Value.Role == "browser" && pair.Value.MetadataComplete && pair.Value.ImageClassification == "installed-main" && pair.Value.CreatedDuringRun == true);
    internal bool RendererVerified => samples.Any(pair => ProtectedExpectedImage(pair.Key,pair.Value, "renderer"));
    internal bool GpuVerified => samples.Any(pair => ProtectedExpectedImage(pair.Key,pair.Value, "gpu"));
    internal bool DefiniteMismatchObserved => identityMismatch||protection.Values.Any(state=>state.Failed)||samples.Any(pair => active.Contains(pair.Key)&&ProtectionGraceExpired(pair.Value,DateTime.UtcNow));
    private bool ImmediateIdentityMismatch(Sample row)=>
        (row.Role == "browser" && row.ImageClassification is not ("unavailable" or "installed-main")) ||
        (row.Role is "renderer" or "gpu" or "utility" && row.ImageClassification != "unavailable" && row.ImageClassification != ExpectedChildClassification) ||
        row.CreatedDuringRun == false;
    internal bool ExpectedPathsVerified => BrowserVerified && RendererVerified && GpuVerified && !DefiniteMismatchObserved;
    internal bool CopyPathVerified => image.UsesCopiedChildImage && ExpectedPathsVerified;
    internal bool OriginalPathVerified => !image.UsesCopiedChildImage && !image.UsesLinkedChildImage && ExpectedPathsVerified;
    internal bool LinkedPathVerified => image.UsesLinkedChildImage && ExpectedPathsVerified;
    internal bool UtilityObserved => samples.Values.Any(row => row.Role == "utility");
    // CDP's generic utility role does not establish that NetworkService was observed.
    internal bool NetworkIndependentlyVerified => false;
    private string ExpectedChildClassification => image.UsesLinkedChildImage ? "linked-original-child" : image.UsesCopiedChildImage ? "verified-copy" : "installed-original-child";
    private bool ProtectedExpectedImage(string key,Sample row, string role) => active.Contains(key)&&row.Role == role && row.MetadataComplete &&
        row.ImageClassification == ExpectedChildClassification && row.CreatedDuringRun == true && row.SignatureFlags == 5&&
        protection.TryGetValue(key,out var state)&&!state.Failed&&state.StableSamples>=2&&state.FirstStableFive.HasValue&&
        row.ObservedAtUtc-state.FirstStableFive.Value>=StableProtectionInterval;
    private static bool ProtectionGraceExpired(Sample row,DateTime now)=>row.Role is "renderer" or "gpu"&&row.SignatureFlags.HasValue&&row.SignatureFlags!=5&&
        (row.SignatureFlags!=0||!row.CreatedAtUtc.HasValue||now-row.CreatedAtUtc.Value>=StartupProtectionGrace);

    internal async Task CaptureAsync(NativeSurfacePipe pipe, CancellationToken cancellation)
    {
        var ownedBefore = pipe.DiagnosticProcessIds.ToHashSet();
        var result = await pipe.SendAsync("SystemInfo.getProcessInfo", cancellation: cancellation).ConfigureAwait(false);
        if (!result.TryGetProperty("processInfo", out var entries) || entries.ValueKind != JsonValueKind.Array) throw new IOException("CDP process metadata unavailable.");
        if (entries.GetArrayLength() > 1024) throw new IOException("CDP process metadata exceeded its diagnostic bound.");
        var ownedNow = pipe.DiagnosticProcessIds.ToHashSet();
        var nextActive=new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();
            if (!entry.TryGetProperty("id", out var id) || !id.TryGetInt32(out var pid) || pid <= 0 || !ownedBefore.Contains(pid) || !ownedNow.Contains(pid)) continue;
            var role = entry.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? Role(type.GetString()) : "other";
            var sample = ReadOwned(pipe, pid, role);
            if (sample is null) continue;
            var key = pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + role;
            nextActive.Add(key);
            samples.TryGetValue(key,out var previous);
            if(ImmediateIdentityMismatch(sample)||samples.Values.Any(old=>old.ProcessId==pid&&old.CreatedAtUtc.HasValue&&
                sample.CreatedAtUtc.HasValue&&old.CreatedAtUtc!=sample.CreatedAtUtc))identityMismatch=true;
            if(previous is not null&&(previous.SignatureFlags!=sample.SignatureFlags||previous.ImageClassification!=sample.ImageClassification||
                previous.MetadataComplete!=sample.MetadataComplete))
            {
                if(transitions.Count>=1024)throw new IOException("Process transition observation exceeded its diagnostic bound.");
                transitions.Add(previous);
            }
            if(role is "renderer" or "gpu")
            {
                if(!protection.TryGetValue(key,out var state))protection[key]=state=new ProtectionState();
                if(sample.SignatureFlags==5)
                {
                    state.SeenFive=true;
                    if(sample.MetadataComplete&&sample.ImageClassification==ExpectedChildClassification&&sample.CreatedDuringRun==true)
                    {state.FirstStableFive??=sample.ObservedAtUtc;state.StableSamples++;}
                    else {state.FirstStableFive=null;state.StableSamples=0;}
                }
                else
                {
                    state.FirstStableFive=null;state.StableSamples=0;
                    if(sample.SignatureFlags.HasValue&&(state.SeenFive||ProtectionGraceExpired(sample,sample.ObservedAtUtc)))state.Failed=true;
                }
            }
            if (samples.Count >= 1024 && !samples.ContainsKey(key)) throw new IOException("Owned process observation exceeded its diagnostic bound.");
            samples[key] = sample;
        }
        active.Clear();active.UnionWith(nextActive);
        captures++;
    }

    private Sample? ReadOwned(NativeSurfacePipe pipe, int pid, string role)
    {
        using var process = OpenProcess(0x1000, false, (uint)pid); // PROCESS_QUERY_LIMITED_INFORMATION only.
        if (process.IsInvalid) return new(pid, role, "unavailable", null, null, null, null, null, false, Marshal.GetLastWin32Error(), "open-owned-process"){ObservedAtUtc=DateTime.UtcNow};
        // Re-query our exact Job after obtaining the handle, before inspecting any process metadata.
        if (!pipe.DiagnosticProcessIds.Contains(pid)) return null;
        DateTime? created = null; bool? duringRun = null; uint? signature = null, integrity = null; bool? appContainer = null;
        var classification = "unavailable"; int? error = null; string? failed = null;
        void Failure(string stage) { error ??= Marshal.GetLastWin32Error(); failed ??= stage; }
        if (GetProcessTimes(process, out var creation, out _, out _, out _))
        {
            var ticks = ((long)creation.High << 32) | creation.Low;
            try { created = DateTime.FromFileTimeUtc(ticks); duringRun = created >= launchStartedUtc.AddSeconds(-1); }
            catch (ArgumentOutOfRangeException) { failed = "invalid-process-creation-time"; }
        }
        else Failure("process-creation-time");
        var path = new StringBuilder(32768); var length = (uint)path.Capacity;
        if (QueryFullProcessImageName(process, 0, path, ref length))
        {
            var actual = Path.GetFullPath(path.ToString());
            if(role=="browser")
                classification=SamePath(actual,image.SourceExecutable)||mainAlias is not null&&SamePath(actual,mainAlias.ChildExecutable)
                    ? "installed-main" : "outside-expected-images";
            else classification = SamePath(actual,image.SourceExecutable)
                ? image.UsesCopiedChildImage ? "installed-main" : ExpectedChildClassification
                : SamePath(actual,image.ChildExecutable)
                    ? image.UsesCopiedChildImage ? "verified-copy" : image.UsesLinkedChildImage&&role!="browser" ? "linked-original-child" : "outside-expected-images"
                    : "outside-expected-images";
        }
        else Failure("process-image-path");
        if (GetProcessMitigationPolicy(process, 8, out var policy, (nuint)sizeof(uint))) signature = policy;
        else Failure("process-signature-policy");
        if (OpenProcessToken(process, 0x0008, out var token)) // TOKEN_QUERY, never token duplication or adjustment.
        {
            using (token)
            {
                var flag = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    if (GetTokenInformation(token, 29, flag, sizeof(uint), out _)) appContainer = Marshal.ReadInt32(flag) != 0;
                    else Failure("token-app-container");
                }
                finally { Marshal.FreeHGlobal(flag); }
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out var required);
                if (required is > 0 and <= 16384)
                {
                    var label = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        if (GetTokenInformation(token, 25, label, required, out _))
                        {
                            var sid = Marshal.ReadIntPtr(label);
                            if (IsValidSid(sid))
                            {
                                var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                                if (count > 0) integrity = unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1)));
                                else failed ??= "invalid-integrity-label";
                            }
                            else failed ??= "invalid-integrity-label";
                        }
                        else Failure("token-integrity-level");
                    }
                    finally { Marshal.FreeHGlobal(label); }
                }
                else Failure("token-integrity-size");
            }
        }
        else Failure("open-process-token");
        var complete = created.HasValue && duringRun.HasValue && classification != "unavailable" && signature.HasValue && appContainer.HasValue && integrity.HasValue && failed is null;
        return new(pid, role, classification, created, duringRun, signature, appContainer, integrity, complete, error, failed){ObservedAtUtc=DateTime.UtcNow};
    }

    private static bool SamePath(string first,string second)=>string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),StringComparison.OrdinalIgnoreCase);
    private static string Role(string? role) => role?.ToLowerInvariant() switch
    { "browser" => "browser", "renderer" => "renderer", "gpu" => "gpu", "utility" => "utility", _ => "other" };
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder imagePath, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy, out uint value, nuint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeFileHandle token, int informationClass, IntPtr value, uint length, out uint required);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsValidSid(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
}
