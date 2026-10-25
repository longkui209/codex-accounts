using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Reversible Windows entry integration. Only an installed source's exact path is registered.</summary>
internal static class WindowsStartupIntegration
{
    private const string Owner="codex-accounts-native-startup-v1";
    private const string ParentKey=@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string FilterName="CodexAccountsLocalNative";
    internal static string InstallationRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Codex Accounts Integration");
    private static string UserSettings=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexAccountSwitcher","startup");
    private static string EnabledFile=>Path.Combine(UserSettings,"enabled.json");
    internal static bool ShouldEnableRouting(bool machineEnabled,bool userEnabled,bool vaultExists,bool pluginInstalled)=>
        userEnabled||machineEnabled&&vaultExists&&pluginInstalled;
    internal sealed record IntegrationStatus(string State,string Message,bool Adaptive=false);
    internal static object Status()
    {
        try
        {
            var source=DesktopLaunch.FindInstalledDesktop();
            using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
            using var root=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe");
            if(root is null)return new IntegrationStatus("not-installed","普通启动组件尚未安装。");
            var filter=FindFilter(root,source);
            var adaptive=StartupVersionService.Running();
            if(filter is null)return new IntegrationStatus("outdated",adaptive?"正在自动跟随官方版本更新；准备完成后请正常重开客户端。":"官方客户端已更新，启动规则尚未匹配；请更新普通启动组件。",adaptive);
            using(filter)
            {
                if(filter.GetValue("CodexAccountsOwner") as string!=Owner ||
                    filter.GetValue("Debugger") is not string command||!TryDebuggerBundle(command,out var bundle)||
                    !Directory.Exists(bundle))return new IntegrationStatus("not-registered","启动注册缺失或归属不符，未确认接入。");
                VerifyMachineDirectory(bundle);
                var manifest=Path.Combine(bundle,"integration.json");NativeRuntimeLease.Plain(manifest);
                if(new FileInfo(manifest).Length>8192)return new IntegrationStatus("not-registered","启动组件清单未通过校验。");
                var metadata=JsonSerializer.Deserialize<NativeRuntimeLease.Manifest>(File.ReadAllBytes(manifest));
                if(metadata is null||!MatchesCurrentInstallation(metadata.SourceExecutable,source)||metadata.SourceHash!=NativeRuntimeLease.Hash(source))
                    return new IntegrationStatus("outdated","启动组件文件与当前官方版本不一致，等待重新核验。",adaptive);
            }
            return new IntegrationStatus("active",adaptive?"版本跟随服务已启用，启动规则匹配当前官方安装；当前窗口连接状态单独核验。":"启动规则匹配当前官方安装；版本跟随服务尚未启用。",adaptive);
        }
        catch{return new IntegrationStatus("unknown","尚未核验当前版本的普通启动状态。");}
    }
    internal static bool MatchesCurrentInstallation(string? registered,string current)=>
        !string.IsNullOrWhiteSpace(registered)&&Path.IsPathFullyQualified(registered)&&
        string.Equals(Path.GetFullPath(registered),Path.GetFullPath(current),StringComparison.OrdinalIgnoreCase);
    internal static string VersionFilterName(string source)=>FilterName+"_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..16].ToLowerInvariant();
    private static RegistryKey? FindFilter(RegistryKey root,string source,bool writable=false)
    {
        foreach(var name in root.GetSubKeyNames())
        {
            if(name!=FilterName&&!name.StartsWith(FilterName+"_",StringComparison.Ordinal))continue;
            var key=root.OpenSubKey(name,writable);
            if(key is not null&&MatchesCurrentInstallation(key.GetValue("FilterFullPath") as string,source))return key;
            key?.Dispose();
        }
        return null;
    }
    private static bool TryDebuggerBundle(string command,out string bundle)
    {
        bundle="";const string suffix="\" --integrated-launch";
        if(!command.StartsWith('"')||!command.EndsWith(suffix,StringComparison.Ordinal))return false;
        var executable=command[1..^suffix.Length];
        if(!Path.IsPathFullyQualified(executable)||Path.GetFileName(executable)!="CodexAccountSwitcher.exe")return false;
        bundle=Path.GetDirectoryName(executable)!;
        return bundle.StartsWith(Path.Combine(InstallationRoot,"versions")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)&&
            Guid.TryParseExact(Path.GetFileName(bundle),"N",out _);
    }
    private static void Record(string stage,string message)
    {
        Directory.CreateDirectory(UserSettings);NativeRuntimeLease.Plain(UserSettings);FilePrivacy.ApplyDirectory(UserSettings);
        var target=Path.Combine(UserSettings,"install-status.json");var temporary=Path.Combine(UserSettings,Guid.NewGuid().ToString("N")+".tmp");
        File.WriteAllText(temporary,JsonSerializer.Serialize(new{state=stage,message,updatedAtUtc=DateTimeOffset.UtcNow}));FilePrivacy.ApplyFile(temporary);
        File.Move(temporary,target,true);
    }
    internal static Process BeginInstall()
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--startup-install-wait");
        return Process.Start(start)??throw new UserFacingException("没有启动 Windows 集成安装。");
    }
    internal static Process BeginUninstall()
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
        start.ArgumentList.Add("--startup-uninstall");
        return Process.Start(start)??throw new UserFacingException("没有启动 Windows 集成恢复。");
    }
    internal static async Task InstallAsync(bool waitForExit,CancellationToken cancellation)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UserFacingException("Windows 启动集成需要一次管理员安装确认。");
        Record("preparing","正在安装版本跟随组件；不会停止当前客户端。");
        try
        {
            EnsureMachineRoot();
            var monitorRoot=Path.Combine(InstallationRoot,"monitor");Directory.CreateDirectory(monitorRoot);VerifyMachineDirectory(monitorRoot);
            var monitor=Path.Combine(monitorRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(monitor);VerifyMachineDirectory(monitor);
            CopyComponent(AppContext.BaseDirectory,monitor);
            StartupVersionService.Install(monitor);
            // Prepare and verify the current version synchronously so installation has a concrete result.
            EnsureVersionRegistered(DesktopLaunch.FindInstalledDesktop(),cancellation);
            Record("active","版本跟随服务已安装，当前版本规则已核验；请正常重开客户端加载订阅连接。");
            await Task.CompletedTask;
        }
        catch{Record("failed","版本跟随组件未完成安装，现有客户端和账号保持不变。");throw;}
    }
    private static void EnsureMachineRoot()
    {
        if(Directory.Exists(InstallationRoot))
        {
            VerifyMachineDirectory(InstallationRoot);
            var marker=Path.Combine(InstallationRoot,".codex-accounts-owner");NativeRuntimeLease.Plain(marker);
            if(File.ReadAllText(marker)!=Owner)throw new IOException("Unknown integration owner.");
        }
        else
        {
            Directory.CreateDirectory(InstallationRoot);SecureMachineDirectory(InstallationRoot);
            File.WriteAllText(Path.Combine(InstallationRoot,".codex-accounts-owner"),Owner);
        }
        var versions=Path.Combine(InstallationRoot,"versions");Directory.CreateDirectory(versions);VerifyMachineDirectory(versions);
    }
    private static void CopyComponent(string from,string destination)
    {
        VerifyMachineDirectory(destination);
        foreach(var name in new[]{"CodexAccountSwitcher.exe","CodexAccountSwitcher.dll","CodexAccountSwitcher.deps.json","CodexAccountSwitcher.runtimeconfig.json"})
        {
            var source=Path.Combine(from,name);NativeRuntimeLease.Plain(source);
            using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
            using(var output=new FileStream(Path.Combine(destination,name),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {input.CopyTo(output);output.Flush(true);}
            if(NativeRuntimeLease.Hash(source)!=NativeRuntimeLease.Hash(Path.Combine(destination,name)))throw new IOException("Component copy changed.");
        }
    }
    internal static void EnsureVersionRegistered(string source,CancellationToken cancellation)
    {
        EnsureMachineRoot();
        using var gate=new Mutex(false,@"Global\CodexAccountsOwnedVersionRegistration");
        var entered=false;
        try
        {
            try{entered=gate.WaitOne(TimeSpan.FromSeconds(90));}catch(AbandonedMutexException){entered=true;}
            if(!entered)throw new IOException("Another version preparation is still running.");
            cancellation.ThrowIfCancellationRequested();
            if(!DesktopLaunch.FindInstalledDesktops(true).Contains(source,StringComparer.OrdinalIgnoreCase))throw new IOException("Installation changed.");
            using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
            using(var root=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe"))
            using(var existing=root is null?null:FindFilter(root,source))
            {
                if(existing is not null)ValidateOwnedFilter(existing);
                if(existing?.GetValue("CodexAccountsOwner") as string==Owner&&existing.GetValue("Debugger") is string command&&TryDebuggerBundle(command,out var prepared)&&
                    File.Exists(Path.Combine(prepared,"integration.json"))&&
                    NativeRuntimeLease.Hash(Path.Combine(prepared,"CodexAccountSwitcher.dll"))==NativeRuntimeLease.Hash(Path.Combine(AppContext.BaseDirectory,"CodexAccountSwitcher.dll")))return;
            }
            var bundle=Path.Combine(InstallationRoot,"versions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(bundle);VerifyMachineDirectory(bundle);
            CopyComponent(AppContext.BaseDirectory,bundle);
            var metadata=NativeRuntimeLease.Create(bundle,typeof(WindowsStartupIntegration).Assembly.GetName().Version?.ToString()??"unknown",source,cancellation);
            cancellation.ThrowIfCancellationRequested();Register(bundle,metadata);
        }
        finally{if(entered)gate.ReleaseMutex();}
    }
    private static void ValidateOwnedFilter(RegistryKey key)
    {
        if(key.GetValue("CodexAccountsOwner") as string!=Owner||key.GetSubKeyNames().Length!=0||
           key.GetValueNames().Any(n=>n is not ("CodexAccountsOwner" or "FilterFullPath" or "Debugger" or "AllUsersEnabled"))||
           key.GetValue("Debugger") is not string command||!TryDebuggerBundle(command,out _))throw new IOException("Startup rule ownership changed.");
    }
    private static void Register(string bundle,NativeRuntimeLease.Manifest metadata)
    {
        var debugger='"'+Path.Combine(bundle,"CodexAccountSwitcher.exe")+"\" --integrated-launch";
        if(debugger.Length>=250)throw new UserFacingException("启动组件路径过长，未登记 Windows 启动规则。");
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var parent=machine.OpenSubKey(ParentKey,true)??throw new IOException();
        using var previous=parent.OpenSubKey("ChatGPT.exe");
        if(previous is not null&&(previous.GetValue("CodexAccountsOwner") as string!=Owner||previous.GetValue("UseFilter") is not int mode||mode!=1||previous.GetValue("Debugger") is not null))
            throw new UserFacingException("存在不属于本组件的启动设置，未覆盖。");
        using var root=previous is null?FreshKey(parent,"ChatGPT.exe"):parent.OpenSubKey("ChatGPT.exe",true)!;
        if(previous is null){root.SetValue("CodexAccountsOwner",Owner,RegistryValueKind.String);root.SetValue("UseFilter",1,RegistryValueKind.DWord);}
        using var current=FindFilter(root,metadata.SourceExecutable,true);
        if(current is not null)
        {
            ValidateOwnedFilter(current);
            var old=current.GetValue("Debugger") as string;
            try{current.SetValue("Debugger",debugger);current.SetValue("AllUsersEnabled",1,RegistryValueKind.DWord);current.Flush();}
            catch{if(current.GetValue("Debugger") as string==debugger&&old is not null)current.SetValue("Debugger",old);throw;}
            return;
        }
        var name=VersionFilterName(metadata.SourceExecutable);
        using var filter=FreshKey(root,name);
        try
        {
            filter.SetValue("CodexAccountsOwner",Owner);filter.SetValue("FilterFullPath",metadata.SourceExecutable);
            filter.SetValue("AllUsersEnabled",1,RegistryValueKind.DWord);filter.SetValue("Debugger",debugger);filter.Flush();root.Flush();
            ValidateOwnedFilter(filter);
            if(!MatchesCurrentInstallation(filter.GetValue("FilterFullPath") as string,metadata.SourceExecutable))throw new IOException();
        }
        catch
        {
            if(filter.GetValue("CodexAccountsOwner") as string==Owner){foreach(var value in filter.GetValueNames())filter.DeleteValue(value);filter.Dispose();root.DeleteSubKey(name,false);}throw;
        }
    }
    internal static void Uninstall()
    {
        StartupVersionService.Remove(); // Stops only the owned version monitor; never stops a user desktop.
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var parent=machine.OpenSubKey(ParentKey,true)??throw new IOException();
        using var root=parent.OpenSubKey("ChatGPT.exe",true);
        if(root is not null)
        {
            foreach(var name in root.GetSubKeyNames().Where(n=>n==FilterName||n.StartsWith(FilterName+"_",StringComparison.Ordinal)))
            {
                using var filter=root.OpenSubKey(name,true)!;ValidateOwnedFilter(filter);
                foreach(var value in filter.GetValueNames())filter.DeleteValue(value);filter.Dispose();root.DeleteSubKey(name,false);
            }
            if(root.GetSubKeyNames().Length==0&&root.GetValue("CodexAccountsOwner") as string==Owner&&root.GetValueNames().All(n=>n is "CodexAccountsOwner" or "UseFilter"))
            {foreach(var name in root.GetValueNames())root.DeleteValue(name);root.Dispose();parent.DeleteSubKey("ChatGPT.exe",false);}
        }
        if(File.Exists(EnabledFile)){NativeRuntimeLease.Plain(EnabledFile);File.Delete(EnabledFile);}
        Record("removed","版本跟随服务及本组件启动规则已移除；运行中的文件保留，当前客户端不会被关闭。");
    }
    internal static async Task LaunchAsync(string source,IReadOnlyList<string> arguments,CancellationToken cancellation)
    {
        var bundle=AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);NativeRuntimeLease.Plain(bundle);
        if(!bundle.StartsWith(Path.Combine(InstallationRoot,"versions")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UserFacingException("启动组件位置未通过核验。");
        VerifyMachineDirectory(InstallationRoot);VerifyMachineDirectory(Path.GetDirectoryName(bundle)!);VerifyMachineDirectory(bundle);
        using var runtime=NativeRuntimeLease.Open(bundle);
        if(!string.Equals(Path.GetFullPath(source),runtime.Metadata.SourceExecutable,StringComparison.OrdinalIgnoreCase))throw new UserFacingException("启动目标与已安装客户端不匹配。");
        if(File.Exists(EnabledFile))NativeRuntimeLease.Plain(EnabledFile);
        var userEnabled=File.Exists(EnabledFile)&&new FileInfo(EnabledFile).Length<128&&File.ReadAllText(EnabledFile)==Owner;
        bool machineEnabled;
        using(var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))
        using(var machineRoot=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe"))
        using(var filter=machineRoot is null?null:FindFilter(machineRoot,source))
            machineEnabled=filter?.GetValue("CodexAccountsOwner") as string==Owner&&filter.GetValue("AllUsersEnabled") is int enabled&&enabled==1;
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var vaultExists=Directory.Exists(Path.Combine(local,"CodexAccountSwitcher","vault"));
        var home=CodexRuntime.ResolveCodexHome();
        var pluginInstalled=Directory.Exists(Path.Combine(home,"plugins","cache","codex-accounts-local","codex-accounts"));
        var enabledForUser=ShouldEnableRouting(machineEnabled,userEnabled,vaultExists,pluginInstalled);
        // A second activation uses Electron's existing singleton; it must not create another relay or change subscriptions.
        if(!enabledForUser||!CodexRuntime.Inspect().CanSwitch)
        {
            var start=new ProcessStartInfo(runtime.Executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(runtime.Executable)!};
            foreach(var argument in arguments)start.ArgumentList.Add(argument);
            _=Process.Start(start)??throw new IOException();return;
        }
        await RoutingHost.RunIntegratedAsync(cancellation,runtime,arguments).ConfigureAwait(false);
    }
    internal static void VerifyMachineDirectory(string path)
    {
        NativeRuntimeLease.Plain(path);
        var security=new DirectoryInfo(path).GetAccessControl();
        var owner=security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(owner is not ("S-1-5-32-544" or "S-1-5-18"))throw new UserFacingException("启动组件目录的系统所有者不符合要求。");
        var write=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.DeleteSubdirectoriesAndFiles;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&write)!=0&&rule.IdentityReference.Value is not ("S-1-5-32-544" or "S-1-5-18"))
                throw new UserFacingException("启动组件目录可被普通用户改写，未登记或执行启动连接。");
    }
    private static void SecureMachineDirectory(string path)
    {
        NativeRuntimeLease.Plain(path);
        var security=new DirectorySecurity();var administrators=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        security.SetOwner(administrators);security.SetAccessRuleProtection(true,false);
        var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        foreach(var sid in new[]{administrators,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,inheritance,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static RegistryKey FreshKey(RegistryKey parent,string name)
    {
        var error=RegCreateKeyEx(parent.Handle,name,0,null,0,0x2011f,IntPtr.Zero,out var handle,out var disposition);
        if(error!=0)throw new System.ComponentModel.Win32Exception(error);
        if(disposition!=1){handle.Dispose();throw new IOException("Startup registration changed concurrently.");}
        return RegistryKey.FromHandle(handle,RegistryView.Registry64);
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern int RegCreateKeyEx(SafeRegistryHandle parent,string name,int reserved,string? @class,int options,int access,IntPtr security,out SafeRegistryHandle result,out int disposition);
}
