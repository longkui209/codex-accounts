using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.ModelRouting;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>One non-waiting snapshot of the forwarded child, never proof from a launch log alone.</summary>
internal static class NativeDiagnosticForwardedChild
{
    internal sealed record Result
    {
        public bool ObservedAlive {get;init;}
        public bool LogValidated {get;init;}
        public bool CreationMatched {get;init;}
        public bool ImageMatched {get;init;}
        public bool IsOwned {get;init;}
        public uint? ProcessId {get;init;}
        public ulong? CreatedFileTime {get;init;}
        public uint? SignatureFlags {get;init;}
        public int? SignaturePolicyError {get;init;}
        public uint? ExitCode {get;init;}
        public string ImageKind {get;init;}="unavailable";
        public DateTime ObservedAtUtc {get;init;}
        public int? Win32Error {get;init;}
        public string? Failure {get;init;}
    }
    private static readonly HashSet<string> LogFields=new(StringComparer.Ordinal)
    {"diagnostic_only","stage","ok","win32_error","unknown_inherited_count","child_pid","child_created_filetime"};

    internal static Result ReadSnapshot(NativeSurfacePipe pipe,NativeDiagnosticChildImage childImage,string outputRoot)
    {
        var result=new Result{ObservedAtUtc=DateTime.UtcNow};
        try
        {
            if(pipe is null||childImage is null||!childImage.UsesLinkedChildImage||childImage.UsesCopiedChildImage||
                string.IsNullOrEmpty(outputRoot)||!Path.IsPathFullyQualified(outputRoot))
                return result with{Failure="unverified-diagnostic-context"};
            var root=Path.GetFullPath(outputRoot);
            if(!SamePath(Path.Combine(root,"p"),childImage.IsolatedRoot))return result with{Failure="output-boundary-mismatch"};
            for(var directory=new DirectoryInfo(root);directory is not null;directory=directory.Parent)
                if(!directory.Exists||(directory.Attributes&FileAttributes.ReparsePoint)!=0)return result with{Failure="output-boundary-not-plain"};
            var path=Path.Combine(root,"crashpad-forwarder-result.json");
            using var file=CreateFile(path,0x80000000,7,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(file.IsInvalid)
            {
                var code=Marshal.GetLastWin32Error();
                return result with{Win32Error=code,Failure=code is 2 or 3?"forwarder-log-missing":"forwarder-log-unavailable"};
            }
            if(!GetFileInformationByHandleEx(file,9,out var attributes,(uint)Marshal.SizeOf<FileAttributeTag>()))
                return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarder-log-attributes-unavailable"};
            if((attributes.Attributes&(0x10u|0x400u))!=0||attributes.ReparseTag!=0)
                return result with{Failure="forwarder-log-not-plain"};
            if(!GetFileSizeEx(file,out var size))return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarder-log-size-unavailable"};
            if(size is <=0 or >4096)return result with{Failure="forwarder-log-size-invalid"};
            var data=new byte[(int)size];
            using(var stream=new FileStream(file,FileAccess.Read,4096,false))
            {
                stream.ReadExactly(data);
                if(stream.ReadByte()!=-1||!GetFileSizeEx(file,out var afterSize)||afterSize!=size)
                    return result with{Failure="forwarder-log-changed-during-read"};
            }
            using var document=JsonDocument.Parse(data,new JsonDocumentOptions{MaxDepth=4,CommentHandling=JsonCommentHandling.Disallow,AllowTrailingCommas=false});
            var log=document.RootElement;
            if(log.ValueKind!=JsonValueKind.Object)return result with{Failure="forwarder-log-schema-invalid"};
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in log.EnumerateObject())
                if(!LogFields.Contains(property.Name)||!names.Add(property.Name))return result with{Failure="forwarder-log-schema-invalid"};
            if(names.Count!=LogFields.Count||log.GetProperty("diagnostic_only").ValueKind!=JsonValueKind.True||
                log.GetProperty("stage").ValueKind!=JsonValueKind.String||log.GetProperty("stage").GetString()!="forward-create"||
                log.GetProperty("ok").ValueKind!=JsonValueKind.True||
                !log.GetProperty("win32_error").TryGetUInt32(out var forwardError)||forwardError!=0||
                !log.GetProperty("unknown_inherited_count").TryGetUInt32(out var inherited)||inherited!=0||
                !log.GetProperty("child_pid").TryGetUInt32(out var pid)||pid==0||pid>int.MaxValue||
                !log.GetProperty("child_created_filetime").TryGetUInt64(out var expectedCreation)||expectedCreation==0)
                return result with{Failure="forwarder-log-not-successful"};
            result=result with{LogValidated=true,ProcessId=pid,CreatedFileTime=expectedCreation};
            if(!pipe.DiagnosticProcessIds.Contains((int)pid))return result with{Failure="forwarded-child-not-in-owned-job"};
            using var process=OpenProcess(0x1000,false,pid); // QUERY_LIMITED_INFORMATION only.
            if(process.IsInvalid)return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarded-child-unavailable"};
            if(GetProcessId(process)!=pid||!pipe.IsOwnedDiagnosticProcessHandle(process.DangerousGetHandle()))
                return result with{Failure="forwarded-child-handle-not-owned"};
            result=result with{IsOwned=true};
            if(!GetProcessTimes(process,out var created,out var exited,out _,out _))
                return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarded-child-creation-unavailable"};
            if(created.Value!=expectedCreation)return result with{Failure="forwarded-child-creation-mismatch"};
            result=result with{CreationMatched=true};
            if(exited.Value!=0)return result with{Failure="forwarded-child-exited"};
            var actualPath=new StringBuilder(32768);var capacity=(uint)actualPath.Capacity;
            if(!QueryFullProcessImageName(process,0,actualPath,ref capacity))
                return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarded-child-image-unavailable"};
            var isAlias=SamePath(actualPath.ToString(),childImage.ChildExecutable);
            var isSource=SamePath(actualPath.ToString(),childImage.SourceExecutable);
            if(!isAlias&&!isSource)return result with{ImageKind="outside-expected-images",Failure="forwarded-child-image-mismatch"};
            result=result with{ImageMatched=true,ImageKind=isAlias?"verified-child-alias":"canonical-original"};
            if(GetProcessMitigationPolicy(process,8,out var signature,(nuint)sizeof(uint)))result=result with{SignatureFlags=signature};
            else result=result with{SignaturePolicyError=Marshal.GetLastWin32Error()};
            if(!GetExitCodeProcess(process,out var exitCode))
                return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarded-child-exit-state-unavailable"};
            result=result with{ExitCode=exitCode};
            if(exitCode!=259)return result with{Failure="forwarded-child-exited"};
            // Exit code 259 alone is ambiguous; an exited process may choose that value.
            // Require a still-zero exit FILETIME and exact creation time on this same handle.
            if(!GetProcessTimes(process,out created,out exited,out _,out _))
                return result with{Win32Error=Marshal.GetLastWin32Error(),Failure="forwarded-child-final-times-unavailable"};
            if(created.Value!=expectedCreation)return result with{CreationMatched=false,Failure="forwarded-child-creation-mismatch"};
            if(exited.Value!=0)return result with{Failure="forwarded-child-exited"};
            if(!pipe.IsOwnedDiagnosticProcessHandle(process.DangerousGetHandle()))
                return result with{IsOwned=false,Failure="forwarded-child-left-owned-job"};
            return result with{ObservedAlive=true,ObservedAtUtc=DateTime.UtcNow,Failure=null};
        }
        catch(JsonException){return result with{Failure="forwarder-log-invalid-json"};}
        catch(InvalidOperationException){return result with{Failure="forwarder-log-invalid-value"};}
        catch{ return result with{Failure="forwarded-child-snapshot-unavailable"}; }
    }

    private static bool SamePath(string first,string second)=>string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),StringComparison.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential)]private struct FileAttributeTag{internal uint Attributes,ReparseTag;}
    [StructLayout(LayoutKind.Sequential)]private struct FileTime
    {internal uint Low,High;internal readonly ulong Value=>((ulong)High<<32)|Low;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string file,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int infoClass,out FileAttributeTag info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file,out long size);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint processId);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process,out FileTime created,out FileTime exited,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder imagePath,ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process,int policy,out uint flags,nuint length);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint exitCode);
}
