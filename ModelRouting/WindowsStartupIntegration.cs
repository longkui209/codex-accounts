using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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
    internal static object Status()
    {
        try
        {
            var path=Path.Combine(UserSettings,"install-status.json");
            if(!File.Exists(path))
            {
                using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
                using var shared=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe\"+FilterName);
                if(shared?.GetValue("CodexAccountsOwner") as string==Owner&&shared.GetValue("AllUsersEnabled") is int enabled&&enabled==1)
                    return new{state="active",message="整机普通启动组件已登记；各 Windows 用户使用自己的登录和账号库，实际连接须打开后核验。"};
                return new{state="not-installed",message="普通启动组件尚未安装。"};
            }
            NativeRuntimeLease.Plain(path);
            if(new FileInfo(path).Length>4096)throw new IOException();
            var recorded=JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(path));
            if(recorded.TryGetProperty("state",out var state)&&state.GetString()=="active")
            {
                using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
                using var filter=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe\"+FilterName);
                if(filter?.GetValue("CodexAccountsOwner") as string!=Owner||filter.GetValue("Debugger") is not string debugger||!debugger.EndsWith(" --integrated-launch",StringComparison.Ordinal))
                    return new{state="not-registered",message="普通启动注册未找到，请重新安装组件。"};
            }
            return recorded;
        }
        catch{return new{state="unknown",message="尚未核验普通启动组件状态。"};}
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
        try
        {
            Record("preparing","正在准备签名原生运行文件。请保存当前任务，准备完成后正常退出所有 Codex 窗口。");
            var root=InstallationRoot;
            if(Directory.Exists(root))
            {
                NativeRuntimeLease.Plain(root);
                var marker=Path.Combine(root,".codex-accounts-owner");
                if(!File.Exists(marker)||File.ReadAllText(marker)!=Owner)throw new UserFacingException("启动组件目录已存在且归属不符，未覆盖。");
                VerifyMachineDirectory(root);
            }
            else
            {
                Directory.CreateDirectory(root);SecureMachineDirectory(root);
                File.WriteAllText(Path.Combine(root,".codex-accounts-owner"),Owner);
            }
            var bundle=Path.Combine(root,"versions",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(bundle);NativeRuntimeLease.Plain(bundle);
            VerifyMachineDirectory(Path.GetDirectoryName(bundle)!);VerifyMachineDirectory(bundle);
            foreach(var name in new[]{"CodexAccountSwitcher.exe","CodexAccountSwitcher.dll","CodexAccountSwitcher.deps.json","CodexAccountSwitcher.runtimeconfig.json"})
            {
                var source=Path.Combine(AppContext.BaseDirectory,name);NativeRuntimeLease.Plain(source);
                using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
                using var output=new FileStream(Path.Combine(bundle,name),FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
            }
            var metadata=NativeRuntimeLease.Create(bundle,typeof(WindowsStartupIntegration).Assembly.GetName().Version?.ToString()??"unknown");
            Record("waiting-exit","原生运行文件已准备。请保存任务并正常退出所有 Codex 窗口；安装完成后会自动打开客户端。");
            var deadline=DateTimeOffset.UtcNow.AddMinutes(15);
            while(!CodexRuntime.Inspect().CanSwitch)
            {
                if(!waitForExit||DateTimeOffset.UtcNow>=deadline)throw new UserFacingException("尚有 Codex 桌面或后台运行，启动注册尚未启用。请正常退出后重新安装。");
                await Task.Delay(500,cancellation).ConfigureAwait(false);
            }
            using(var runtime=NativeRuntimeLease.Open(bundle))Register(bundle,runtime.Metadata);
            Directory.CreateDirectory(UserSettings);FilePrivacy.ApplyDirectory(UserSettings);
            File.WriteAllText(EnabledFile,Owner);FilePrivacy.ApplyFile(EnabledFile);
            Record("active","整机普通启动组件已安装，所有 Windows 用户的匹配入口均会接入；账号库按用户隔离，实际连接须打开后核验。");
            var launch=new ProcessStartInfo(metadata.SourceExecutable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(metadata.SourceExecutable)!,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
            _=Process.Start(launch)??throw new IOException();
        }
        catch
        {
            Record("failed","启动组件安装没有完成。已准备文件不代表普通入口已接通；请查看本次安装状态后重试。");
            throw;
        }
    }
    private static void Register(string bundle,NativeRuntimeLease.Manifest metadata)
    {
        var debugger='"'+Path.Combine(bundle,"CodexAccountSwitcher.exe")+"\" --integrated-launch";
        if(debugger.Length>=250)throw new UserFacingException("启动组件路径过长，未登记 Windows 启动规则。");
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var parent=machine.OpenSubKey(ParentKey,true)??throw new IOException();
        using var existing=parent.OpenSubKey("ChatGPT.exe");
        if(existing is not null)throw new UserFacingException("ChatGPT.exe 已有启动规则，未覆盖。请先卸载已有普通启动组件再安装。");
        using var root=FreshKey(parent,"ChatGPT.exe");var filterCreated=false;
        try
        {
            root.SetValue("CodexAccountsOwner",Owner,RegistryValueKind.String);root.SetValue("UseFilter",1,RegistryValueKind.DWord);
            using var filter=FreshKey(root,FilterName);filterCreated=true;
            filter.SetValue("CodexAccountsOwner",Owner,RegistryValueKind.String);
            filter.SetValue("FilterFullPath",metadata.SourceExecutable,RegistryValueKind.String);
            filter.SetValue("AllUsersEnabled",1,RegistryValueKind.DWord);
            filter.SetValue("Debugger",debugger,RegistryValueKind.String);filter.Flush();root.Flush();
            if(filter.GetValue("Debugger") as string!=debugger||filter.GetValue("FilterFullPath") as string!=metadata.SourceExecutable)throw new IOException();
        }
        catch
        {
            if(filterCreated){using var filter=root.OpenSubKey(FilterName,true);if(filter?.GetValue("CodexAccountsOwner") as string==Owner){foreach(var name in filter.GetValueNames())filter.DeleteValue(name);filter.Dispose();root.DeleteSubKey(FilterName,false);}}
            if(root.GetSubKeyNames().Length==0&&root.GetValue("CodexAccountsOwner") as string==Owner){foreach(var name in root.GetValueNames())root.DeleteValue(name);root.Dispose();parent.DeleteSubKey("ChatGPT.exe",false);}
            throw;
        }
    }
    internal static void Uninstall()
    {
        using var machine=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var parent=machine.OpenSubKey(ParentKey,true)??throw new IOException();
        using var root=parent.OpenSubKey("ChatGPT.exe",true);
        if(root is not null)
        {
            using var filter=root.OpenSubKey(FilterName,true);
            if(filter is not null)
            {
                var debugger=filter.GetValue("Debugger") as string;
                if(filter.GetValue("CodexAccountsOwner") as string!=Owner||filter.GetSubKeyNames().Length!=0||
                    filter.GetValueNames().Any(name=>name is not ("CodexAccountsOwner" or "FilterFullPath" or "Debugger" or "AllUsersEnabled"))||
                    debugger is null||!debugger.StartsWith('"'+Path.Combine(InstallationRoot,"versions")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new UserFacingException("启动规则已改变，未删除不明配置。");
                filter.DeleteValue("Debugger",false);filter.DeleteValue("AllUsersEnabled",false);filter.DeleteValue("FilterFullPath",false);filter.DeleteValue("CodexAccountsOwner",false);filter.Dispose();root.DeleteSubKey(FilterName,false);
            }
            if(root.GetSubKeyNames().Length==0&&root.GetValue("CodexAccountsOwner") as string==Owner&&
                root.GetValueNames().All(name=>name is "CodexAccountsOwner" or "UseFilter"))
            {foreach(var name in root.GetValueNames())root.DeleteValue(name);root.Dispose();parent.DeleteSubKey("ChatGPT.exe",false);}
        }
        if(File.Exists(EnabledFile)){NativeRuntimeLease.Plain(EnabledFile);File.Delete(EnabledFile);}
        Record("removed","普通启动注册已移除，原版入口已恢复。正在使用的本地运行文件保留，待客户端退出后可清理。");
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
        using(var filter=machine.OpenSubKey(ParentKey+@"\ChatGPT.exe\"+FilterName))
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
    private static void VerifyMachineDirectory(string path)
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
