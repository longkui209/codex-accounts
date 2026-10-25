using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using CodexAccountSwitcher.LiveSession;
using Microsoft.Win32;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Machine installation maintenance only. Never creates a vault, reads auth or launches a user desktop.</summary>
internal static class StartupVersionService
{
    internal const string Name="CodexAccountsVersionUpdate";
    private const string Argument="--startup-version-service";
    private static readonly CancellationTokenSource Stop=new();
    private static IntPtr statusHandle;
    private static ServiceMain? main;
    private static Handler? handler;
    private static readonly object StatusGate=new();
    private static uint? lastControl;
    private static string StateFile=>Path.Combine(WindowsStartupIntegration.InstallationRoot,"version-monitor.json");
    internal static bool Installed()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name);
        return key is not null&&IsOwnedCommand(key.GetValue("ImagePath") as string);
    }
    internal static bool Running()
    {
        try
        {
            if(!Installed())return false;
            using var manager=OpenSCManager(null,null,1);if(manager.IsInvalid)return false;
            using var service=OpenService(manager,Name,4);
            return !service.IsInvalid&&QueryServiceStatus(service,out var status)&&status.CurrentState==4;
        }
        catch{return false;}
    }
    internal static bool IsOwnedCommand(string? command)
    {
        if(command is null||!command.StartsWith('"')||!command.EndsWith("\" "+Argument,StringComparison.Ordinal))return false;
        var image=command[1..^(Argument.Length+2)];
        var root=Path.Combine(WindowsStartupIntegration.InstallationRoot,"monitor")+Path.DirectorySeparatorChar;
        return Path.IsPathFullyQualified(image)&&image.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&
            Path.GetFileName(image)=="CodexAccountSwitcher.exe"&&Path.GetFileName(Path.GetDirectoryName(image)!) is {Length:32} id&&Guid.TryParseExact(id,"N",out _);
    }
    internal static void Install(string protectedDirectory)
    {
        WindowsStartupIntegration.VerifyMachineDirectory(protectedDirectory);
        var executable=Path.Combine(protectedDirectory,"CodexAccountSwitcher.exe");NativeRuntimeLease.Plain(executable);
        var command='"'+executable+"\" "+Argument;
        if(!IsOwnedCommand(command))throw new IOException("Invalid protected monitor path.");
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name);
        if(key is not null&&!IsOwnedCommand(key.GetValue("ImagePath") as string))throw new IOException("An unrelated service has this name.");
        if(key is not null)
        {
            // Windows can return CANNOT_ACCEPT_CTRL while the owned service is already
            // transitioning to stopped. The subsequent SCM check must still confirm stop.
            Sc(new[]{"stop",Name},new[]{0,1062,1061});
            WaitStopped();
            Sc(new[]{"config",Name,"binPath=",command,"start=","auto","obj=","LocalSystem"},new[]{0});
        }
        else Sc(new[]{"create",Name,"binPath=",command,"start=","auto","obj=","LocalSystem","DisplayName=","Codex Accounts Version Update"},new[]{0});
        Sc(new[]{"description",Name,"Maintains verified Codex Accounts startup registration when the official installation changes. Does not access saved accounts or chats."},new[]{0});
        Sc(new[]{"failure",Name,"reset=","86400","actions=","restart/60000/restart/60000/restart/300000"},new[]{0});
        Sc(new[]{"failureflag",Name,"1"},new[]{0});
        Sc(new[]{"start",Name},new[]{0,1056});
    }
    internal static void Remove()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name);
        if(key is null)return;
        if(!IsOwnedCommand(key.GetValue("ImagePath") as string))throw new IOException("Refusing an unrelated service.");
        Sc(new[]{"stop",Name},new[]{0,1062,1061});WaitStopped();Sc(new[]{"delete",Name},new[]{0});
    }
    private static void WaitStopped()
    {
        using var manager=OpenSCManager(null,null,1);if(manager.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service=OpenService(manager,Name,4);if(service.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var until=Stopwatch.StartNew();
        do{if(!QueryServiceStatus(service,out var status))throw new Win32Exception(Marshal.GetLastWin32Error());if(status.CurrentState==1)return;Thread.Sleep(200);}while(until.Elapsed<TimeSpan.FromSeconds(40));
        throw new IOException("Version monitor did not stop; no user process was stopped.");
    }
    private static void Sc(string[] arguments,int[] allowed)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe"))
            {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException();
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))throw new IOException("Owned service-control request timed out.");
        _=output.GetAwaiter().GetResult();_=error.GetAwaiter().GetResult();
        if(!allowed.Contains(process.ExitCode))throw new Win32Exception(process.ExitCode);
    }
    internal static void Run()
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!identity.IsSystem||!IsOwnedCommand('"'+Environment.ProcessPath+"\" "+Argument))throw new IOException("Version monitor must run from its protected installed service.");
        WindowsStartupIntegration.VerifyMachineDirectory(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        main=ServiceEntryPoint;handler=Control;
        var table=new[]{new ServiceEntry{Name=Name,Main=Marshal.GetFunctionPointerForDelegate(main)},new ServiceEntry()};
        if(!StartServiceCtrlDispatcher(table))throw new Win32Exception(Marshal.GetLastWin32Error());
        GC.KeepAlive(main);GC.KeepAlive(handler);
    }
    private static void ServiceEntryPoint(uint count,IntPtr arguments)
    {
        statusHandle=RegisterServiceCtrlHandler(Name,handler!);if(statusHandle==IntPtr.Zero)return;
        Report(2);Report(4);
        uint exit=0;
        try{WriteState("starting",null);MonitorAsync(Stop.Token).GetAwaiter().GetResult();}
        catch(OperationCanceledException)when(Stop.IsCancellationRequested){}
        catch{exit=1;WriteState("failed",null);}
        finally{if(exit==0)WriteState("stopped",null);Report(1,exit);}
    }
    private static void Control(uint command)
    {
        if(command is 1 or 5){lastControl=command;WriteState("stopping",null);Report(3);Stop.Cancel();}
        else if(command==4)Report(Stop.IsCancellationRequested?3u:4u);
    }
    private static void Report(uint state,uint exit=0)
    {
        lock(StatusGate)
        {
            var status=new ServiceStatus{ServiceType=16,CurrentState=state,ControlsAccepted=state==4?5u:0,WaitHint=state is 2 or 3?45000u:0,Win32ExitCode=exit};
            _=SetServiceStatus(statusHandle,ref status);
        }
    }
    private static async Task MonitorAsync(CancellationToken cancellation)
    {
        var last="";var lastGood=DateTimeOffset.MinValue;
        while(!cancellation.IsCancellationRequested)
        {
            try
            {
                var sources=DesktopLaunch.FindInstalledDesktops(true).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var fingerprint=string.Join('\n',sources);
                if(fingerprint!=last||DateTimeOffset.UtcNow-lastGood>TimeSpan.FromMinutes(5))
                {
                    WriteState("checking",sources.Length);
                    foreach(var source in sources)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        WindowsStartupIntegration.EnsureVersionRegistered(source,cancellation);
                    }
                    last=fingerprint;lastGood=DateTimeOffset.UtcNow;WriteState("ready",sources.Length);
                }
                else WriteState("ready",sources.Length);
            }
            catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}
            catch{WriteState("retrying",null);}
            await Task.Delay(TimeSpan.FromSeconds(10),cancellation).ConfigureAwait(false);
        }
    }
    private static void WriteState(string state,int? count)
    {
        try
        {
            WindowsStartupIntegration.VerifyMachineDirectory(WindowsStartupIntegration.InstallationRoot);
            var temporary=StateFile+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new{state,registeredVersions=count,updatedAtUtc=DateTimeOffset.UtcNow,lastControl,accountDataAccessed=false}));
            File.Move(temporary,StateFile,true);
        }
        catch{ }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate void ServiceMain(uint argc,IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate void Handler(uint control);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ServiceEntry{[MarshalAs(UnmanagedType.LPWStr)]internal string? Name;internal IntPtr Main;}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatus{internal uint ServiceType,CurrentState,ControlsAccepted,Win32ExitCode,ServiceSpecificExitCode,CheckPoint,WaitHint;}
    private sealed class ServiceHandle:Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {private ServiceHandle():base(true){}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr RegisterServiceCtrlHandler(string name,Handler handler);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetServiceStatus(IntPtr handle,ref ServiceStatus status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ServiceHandle OpenSCManager(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryServiceStatus(ServiceHandle service,out ServiceStatus status);
    [DllImport("advapi32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseServiceHandle(IntPtr handle);
}
