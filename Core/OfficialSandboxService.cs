using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CodexAccountSwitcher.LiveSession;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

/// <summary>Recognizes only the signed, SCM-owned system sandbox provisioner. Never stops or reconfigures it.</summary>
internal static class OfficialSandboxService
{
    internal const string ServiceName="CodexSandboxService.OpenAI.Codex";
    internal const string ImageName="codex-windows-sandbox-service";
    private static readonly object Gate=new();
    private static (int Pid,long Start,string Path,DateTimeOffset At)? verified;
    internal static string LastVerificationStage {get;private set;}="not-observed";
    internal static bool MetadataMatches(string service,string account,uint state,uint servicePid,int pid,string registered,string actual,string windowsApps)
    {
        if(service!=ServiceName||account!="LocalSystem"||state!=4||pid<=0||servicePid!=pid||
            !Path.IsPathFullyQualified(registered)||!Path.IsPathFullyQualified(actual)||
            !string.Equals(Path.GetFullPath(registered),Path.GetFullPath(actual),StringComparison.OrdinalIgnoreCase))return false;
        var relative=Path.GetRelativePath(windowsApps,registered);
        return Regex.IsMatch(relative,@"\AOpenAI\.Codex_[0-9]+(?:\.[0-9]+){3}_x64__2p2nqsd0c76g0[\\/]app[\\/]resources[\\/]codex-windows-sandbox-service\.exe\z",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    }
    internal static bool IsVerifiedDaemon(Process process)
    {
        if(!process.ProcessName.Equals(ImageName,StringComparison.OrdinalIgnoreCase))return false;
        try
        {
            LastVerificationStage="process-start";
            var started=process.StartTime.ToUniversalTime().Ticks;
            lock(Gate)
            {
                if(verified is { } cached&&cached.Pid==process.Id&&cached.Start==started&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromSeconds(5))return true;
                LastVerificationStage="scm-open";
                using var scm=OpenSCManager(null,null,1);if(scm.IsInvalid)return false;
                using var service=OpenService(scm,ServiceName,4);if(service.IsInvalid)return false;
                LastVerificationStage="service-status";
                if(!QueryServiceStatusEx(service,0,out var status,Marshal.SizeOf<ServiceProcessStatus>(),out _)||status.State!=4||status.Pid!=process.Id)return false;
                LastVerificationStage="registration";
                using var registration=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+ServiceName);
                var command=registration?.GetValue("ImagePath") as string;
                var match=Regex.Match(command??"","\\A\"(?<image>[^\"]+)\"(?: --service)?\\z");if(!match.Success)return false;
                LastVerificationStage="process-image";
                using var handle=OpenProcess(0x1000,false,process.Id);if(handle.IsInvalid)return false;
                var capacity=32768;var image=new char[capacity];if(!QueryFullProcessImageName(handle,0,image,ref capacity))return false;
                var actual=new string(image,0,capacity);var registered=match.Groups["image"].Value;
                var windowsApps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"WindowsApps");
                LastVerificationStage="metadata";
                if(!MetadataMatches(ServiceName,registration?.GetValue("ObjectName") as string??"",status.State,status.Pid,process.Id,registered,actual,windowsApps))return false;
                LastVerificationStage="signature";
                DesktopLaunch.ValidateSandboxServiceImage(actual);
                LastVerificationStage="stability";
                if(!QueryServiceStatusEx(service,0,out var after,Marshal.SizeOf<ServiceProcessStatus>(),out _)||after.State!=4||after.Pid!=process.Id||process.StartTime.ToUniversalTime().Ticks!=started)return false;
                verified=(process.Id,started,actual,DateTimeOffset.UtcNow);LastVerificationStage="verified";return true;
            }
        }
        catch{return false;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct ServiceProcessStatus{internal uint Type,State,Accepted,Exit,ServiceExit,Checkpoint,Hint,Pid,Flags;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid{private ServiceHandle():base(true){}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out ServiceProcessStatus status,int size,out int needed);
    [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,[Out]char[] image,ref int size);
}
