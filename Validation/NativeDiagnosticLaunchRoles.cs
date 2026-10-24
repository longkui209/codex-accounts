using System.Runtime.InteropServices;
using System.Text;
using CodexAccountSwitcher.ModelRouting;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Validation;

/// <summary>Classifies only this new Job's launch roles. Argument values never leave this method.</summary>
internal static class NativeDiagnosticLaunchRoles
{
    internal sealed record Sample(int ProcessId,string Role,string ImageKind,bool ExactJobVerified,int? Error);
    internal static IReadOnlyList<Sample> Capture(NativeSurfacePipe pipe,NativeDiagnosticChildImage child,NativeDiagnosticChildImage? main)
    {
        var results=new List<Sample>();
        foreach(var pid in pipe.DiagnosticProcessIds.Take(128))
        {
            using var process=OpenProcess(0x1000,false,(uint)pid);
            if(process.IsInvalid||!pipe.IsOwnedDiagnosticProcessHandle(process.DangerousGetHandle()))continue;
            var path=new StringBuilder(32768);var size=(uint)path.Capacity;
            if(!QueryFullProcessImageName(process,0,path,ref size))continue;
            var actual=path.ToString();
            var image=Same(actual,child.ChildExecutable)?"child-alias":main is not null&&Same(actual,main.ChildExecutable)?"main-alias":Same(actual,child.SourceExecutable)?"original-installed":"other-owned-image";
            if(image=="other-owned-image")continue;
            var memory=Marshal.AllocHGlobal(65536);
            try
            {
                var status=NtQueryInformationProcess(process,60,memory,65536,out var needed);
                if(status<0){results.Add(new(pid,"unavailable",image,true,status));continue;}
                var header=Marshal.PtrToStructure<Unicode>(memory);var start=memory.ToInt64();var text=header.Buffer.ToInt64();
                if(header.Length%2!=0||text<start||text>start+65536-header.Length||needed>65536)
                {results.Add(new(pid,"unavailable",image,true,13));continue;}
                var command=Marshal.PtrToStringUni(header.Buffer,header.Length/2)??"";
                var count=0;var arguments=CommandLineToArgv(command,out count);var role="browser";
                try
                {
                    if(arguments==IntPtr.Zero||count is <1 or >256)role="unavailable";
                    else for(var i=1;i<count;i++)
                    {
                        var value=Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments,i*IntPtr.Size));
                        if(value?.StartsWith("--type=",StringComparison.Ordinal)==true)
                            role=value switch{"--type=crashpad-handler"=>"crashpad-handler","--type=gpu-process"=>"gpu","--type=renderer"=>"renderer","--type=utility"=>"utility",_=>"other-type"};
                    }
                }
                finally{if(arguments!=IntPtr.Zero)LocalFree(arguments);}
                if(pipe.IsOwnedDiagnosticProcessHandle(process.DangerousGetHandle()))results.Add(new(pid,role,image,true,null));
            }
            finally
            {
                // The buffer briefly contains only this isolated launch's command line.
                for(var offset=0;offset<65536;offset+=8)Marshal.WriteInt64(memory,offset,0);
                Marshal.FreeHGlobal(memory);
            }
        }
        return results;
    }
    private static bool Same(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential)]private struct Unicode{internal ushort Length,MaximumLength;internal IntPtr Buffer;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint pid);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint size);
    [DllImport("ntdll.dll")]private static extern int NtQueryInformationProcess(SafeProcessHandle process,int infoClass,IntPtr buffer,uint length,out uint returned);
    [DllImport("shell32.dll",EntryPoint="CommandLineToArgvW",CharSet=CharSet.Unicode)]private static extern IntPtr CommandLineToArgv(string command,out int count);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
}
