using System.IO;
using System.Text.Json;
using System.Windows;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.PluginHost;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Length==1 && e.Args[0] is "--startup-install-wait" or "--startup-uninstall")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunStartupOperationAsync(e.Args[0]);return;
        }
        if(e.Args.Length>=2 && e.Args[0]=="--integrated-launch")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunIntegratedLaunchAsync(e.Args[1],e.Args.Skip(2).ToArray());return;
        }
        if(e.Args.Length==2 && e.Args[0]=="--native-entry-spec")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try
            {
                var specFile=Path.Combine(AppContext.BaseDirectory,"native-entry-spec.json");
                if((File.GetAttributes(specFile)&FileAttributes.ReparsePoint)!=0||new FileInfo(specFile).Length>4096){Shutdown(1);return;}
                using var spec=JsonDocument.Parse(File.ReadAllBytes(specFile));
                var root=spec.RootElement.GetProperty("output").GetString()!;
                var copy=spec.RootElement.GetProperty("copy").GetString()!;
                var target=spec.RootElement.GetProperty("target").GetString()!;
                if(!string.Equals(Path.GetFullPath(e.Args[1]),target,StringComparison.OrdinalIgnoreCase)||
                    !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root),"^native-entry-copy-[0-9a-f]{32}$")){Shutdown(1);return;}
                _=RunNativeChildSurfaceSmokeAsync(root,copy,false,false,false,null,false,true);return;
            }
            catch{Shutdown(1);return;}
        }
        if(e.Args.Length>=1 && e.Args[0]=="--native-intercepted-runtime-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(e.Args.Length>=3)
            {
                var parent=Path.GetDirectoryName(e.Args[1]);
                if(parent is not null&&Path.GetFileName(parent)=="validation-output"&&File.Exists(Path.Combine(Path.GetDirectoryName(parent)!,"CodexAccountSwitcher.csproj")))
                    File.WriteAllText(Path.Combine(parent,"native-entry-invocation-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(new{argument_count=e.Args.Length,ifeo_index=Array.IndexOf(e.Args,"--ifeo")}));
            }
            if(e.Args.Length!=5||e.Args[3]!="--ifeo"){Shutdown(1);return;}
            var target=Path.GetFullPath(e.Args[4]);
            var targetOwner=Path.GetDirectoryName(target)!;
            if(Path.GetFileName(target) is not ("ChatGPT.exe" or "CodexAccountsOfficialEntryProbe.exe")||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(targetOwner),"^official-entry-copy-[0-9a-f]{32}$")||
                !string.Equals(Path.GetDirectoryName(targetOwner),Path.GetDirectoryName(e.Args[1]),StringComparison.OrdinalIgnoreCase)){Shutdown(1);return;}
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[2],false,false,false,null,false,true);return;
        }
        if(e.Args.Length==3 && e.Args[0]=="--native-copied-runtime-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[2],false,false,false,null,false,true);return;
        }
        if(e.Args.Length==4 && e.Args[0]=="--native-linked-crashpad-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[3],false,true,true,e.Args[2],true);return;
        }
        if(e.Args.Length==4 && e.Args[0]=="--native-linked-main-control")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[3],false,true,true,e.Args[2]);return;
        }
        if(e.Args.Length==3 && e.Args[0]=="--native-linked-detach-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[2],false,true,true);return;
        }
        if(e.Args.Length==3 && e.Args[0]=="--native-linked-surface-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[2],false,true);return;
        }
        if(e.Args.Length==2 && e.Args[0]=="--native-original-surface-control")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],null,true);return;
        }
        if(e.Args.Length==3 && e.Args[0]=="--native-child-surface-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeChildSurfaceSmokeAsync(e.Args[1],e.Args[2]);return;
        }
        if(e.Args.Length==2 && e.Args[0]=="--native-surface-smoke")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            _=RunNativeSurfaceSmokeAsync(e.Args[1]);return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--deferred-protocol-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunDeferredProtocolTestAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--deferred-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunDeferredWorkerTestAsync(e.Args[1], e.Args[2]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--routing-diagnose")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunRoutingDiagnosisAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length is 2 or 3 && e.Args[0] == "--routing-probe")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunRoutingProbeAsync(e.Args[1], e.Args.Length == 3 ? e.Args[2] : null);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--routing-launch")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunRoutingSessionAsync();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--routing-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunRoutingTestAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--live-launch")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunLiveSessionAsync();
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--live-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunLiveTestAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--mcp")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunPluginServerAsync(null);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--mcp-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunPluginServerAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--deferred" or "--plugin-login")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunPluginWorkerAsync(e.Args[0], e.Args[1]);
            return;
        }
        // Test and UI-preview modes never load a real account or touch the user's auth.json.
        if (e.Args.Length == 2 && e.Args[0] == "--self-test")
        {
            RunSelfTests(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose")
        {
            RunReadOnlyDiagnosis(e.Args[1]);
            return;
        }
        if(e.Args.Any(argument=>argument.StartsWith("--native-",StringComparison.Ordinal))){Shutdown(1);return;}
        DispatcherUnhandledException += (_, args) =>
        {
            var text = args.Exception is UserFacingException known ? known.Message : "操作未能完成。请重新打开工具，当前登录缓存不会被强制覆盖。";
            MessageBox.Show(text, "Codex 账号切换器", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        var previewExecutable = Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.EndsWith(".Preview", StringComparison.OrdinalIgnoreCase) == true;
        var window = new MainWindow(e.Args.Contains("--demo") || previewExecutable);
        MainWindow = window;
        window.Show();
    }

    private async Task RunNativeSurfaceSmokeAsync(string root)
    {try{await Task.Run(()=>Validation.NativeSurfaceSmoke.RunAsync(root));Shutdown(0);}catch{Shutdown(1);}}

    private async Task RunNativeChildSurfaceSmokeAsync(string root,string? childExecutable,bool originalChildControl=false,bool linkedChild=false,bool debugDetach=false,string? mainAliasExecutable=null,bool requireForwardedChild=false,bool useCopiedMain=false)
    {try{await Task.Run(()=>Validation.NativeSurfaceSmoke.RunChildImageAsync(root,childExecutable,originalChildControl,linkedChild,debugDetach,mainAliasExecutable,requireForwardedChild,useCopiedMain));Shutdown(0);}catch{Shutdown(1);}}

    private async Task RunDeferredProtocolTestAsync(string root)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await Validation.DeferredWorkerProtocolHarness.RunParentAsync(root, input, output);
            Shutdown(0);
        }
        catch { Shutdown(1); }
    }
    private async Task RunDeferredWorkerTestAsync(string root, string requestId)
    {
        try { Shutdown(await Validation.DeferredWorkerProtocolHarness.RunWorkerAsync(root, requestId)); }
        catch { Shutdown(1); }
    }
    private async Task RunRoutingDiagnosisAsync(string path)
    {
        try { await Task.Run(() => Validation.RoutingDiagnosis.RunAsync(path)); Shutdown(0); }
        catch { Shutdown(1); }
    }
    private async Task RunRoutingProbeAsync(string path, string? profile)
    {
        try { await Task.Run(() => Validation.RoutingDiagnosis.RunSubscriptionProbeAsync(path, profile)); Shutdown(0); }
        catch { Shutdown(1); }
    }
    private async Task RunRoutingSessionAsync()
    {
        try { await Task.Run(() => RoutingHost.RunAsync(CancellationToken.None)); Shutdown(0); }
        catch (Exception error)
        {
            MessageBox.Show(error is UserFacingException safe ? safe.Message : "订阅切换接入失败。普通 Codex 入口、登录和配置保留，请使用普通入口恢复。",
                "Codex 订阅切换", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        }
    }
    private async Task RunStartupOperationAsync(string operation)
    {
        try
        {
            await Task.Run(async()=>
            {
                if(operation=="--startup-uninstall")WindowsStartupIntegration.Uninstall();
                else await WindowsStartupIntegration.InstallAsync(true,CancellationToken.None);
            });
            Shutdown(0);
        }
        catch(Exception error)
        {
            MessageBox.Show(error is UserFacingException known?known.Message:"Windows 启动集成未完成，请查看面板中的安装状态。","Codex 普通启动",MessageBoxButton.OK,MessageBoxImage.Information);
            Shutdown(1);
        }
    }
    private async Task RunIntegratedLaunchAsync(string target,IReadOnlyList<string> arguments)
    {
        try{await Task.Run(()=>WindowsStartupIntegration.LaunchAsync(target,arguments,CancellationToken.None));Shutdown(0);}
        catch(Exception error)
        {
            MessageBox.Show(error is UserFacingException known?known.Message:"普通启动接入未完成。可从账号插件中恢复启动注册后重试。","Codex 普通启动",MessageBoxButton.OK,MessageBoxImage.Information);
            Shutdown(1);
        }
    }
    private async Task RunRoutingTestAsync(string root)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await Validation.RoutingProtocolHarness.RunAsync(root, input, output);
            Shutdown(0);
        }
        catch { Shutdown(1); }
    }
    private async Task RunLiveSessionAsync()
    {
        try { await LiveSessionHost.RunAsync(CancellationToken.None); Shutdown(0); }
        catch (Exception error)
        {
            MessageBox.Show(error is UserFacingException safe ? safe.Message : "账号直切接入未能完成。普通 Codex 入口和登录缓存保留，请使用普通入口恢复。",
                "Codex 账号直切", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        }
    }
    private async Task RunLiveTestAsync(string root)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            await Validation.LiveProtocolHarness.RunAsync(root, input, output);
            Shutdown(0);
        }
        catch { Shutdown(1); }
    }

    private async Task RunPluginServerAsync(string? isolatedRoot)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            using var server = new McpServer(isolatedRoot);
            await server.RunAsync(input, output);
            Shutdown(0);
        }
        catch { Shutdown(1); }
    }

    private async Task RunPluginWorkerAsync(string mode, string requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) { Shutdown(1); return; }
        try
        {
            var vault = McpServer.CreateVault();
            var result = mode == "--deferred"
                ? await new DeferredSwitchService(vault, CodexRuntime.ResolveCodexHome()).RunWorkerAsync(requestId, CancellationToken.None)
                : await new PluginLoginService(vault).RunWorkerAsync(requestId);
            Shutdown(result);
        }
        catch
        {
            if (mode == "--deferred")
            {
                try { new DeferredSwitchService(McpServer.CreateVault(), CodexRuntime.ResolveCodexHome()).RecordWorkerFailure(requestId, "startup-failed", 1); } catch { }
            }
            Shutdown(1);
        }
    }

    private void RunSelfTests(string outputRoot)
    {
        var safeRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(safeRoot);
        try
        {
            var messages = Validation.VaultSelfTests.Run(safeRoot)
                .Concat(Validation.SwitchSelfTests.Run(safeRoot))
                .Concat(Validation.DeferredSelfTests.Run(safeRoot)).ToArray();
            messages = messages.Concat(Validation.LiveCredentialSelfTests.Run(safeRoot)).ToArray();
            messages = messages.Concat(Validation.RouterCredentialSelfTests.Run(safeRoot))
                .Concat(Validation.NativeRoutingLaunchSelfTests.Run())
                .Concat(ConnectionNominations.ParserSelfTests())
                .Concat(Validation.NativeDesktopLifetimeSelfTests.Run()).ToArray();
            messages = messages.Concat(Validation.AccountUsageSelfTests.RunAsync(safeRoot).GetAwaiter().GetResult()).ToArray();
            messages = messages.Concat(Validation.AccountResetSelfTests.RunAsync(safeRoot).GetAwaiter().GetResult()).ToArray();
            messages = messages.Concat(Validation.ResponseCompletionSelfTests.RunAsync().GetAwaiter().GetResult()).ToArray();
            messages = messages.Concat(Validation.RotationSelfTests.RunAsync(safeRoot).GetAwaiter().GetResult()).ToArray();
            messages = messages.Concat(Validation.DesktopQuotaPreflightSelfTests.Run()).ToArray();
            messages = messages.Concat(Validation.DesktopLoginSwitchSelfTests.RunAsync(safeRoot).GetAwaiter().GetResult()).ToArray();
            messages = messages.Concat(Validation.AccountProfileSelfTests.Run()).ToArray();
            messages = messages.Concat(Validation.NativeInputSelfTests.Run()).ToArray();
            messages = messages.Concat(Validation.NativeBrowserRegistrationGuardSelfTests.Run(safeRoot)).ToArray();
            messages = messages.Concat(Validation.WindowsStartupIntegrationSelfTests.Run()).ToArray();
            File.WriteAllText(Path.Combine(safeRoot, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = messages }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(0);
        }
        catch (Exception failure)
        {
            File.WriteAllText(Path.Combine(safeRoot, "result.json"), JsonSerializer.Serialize(new { passed = false, error_type = failure.GetType().Name, message = failure is UserFacingException safe ? safe.Message : "隔离验证失败；未访问真实账号。" }));
            Shutdown(1);
        }
    }

    private void RunReadOnlyDiagnosis(string outputFile)
    {
        var supported = false;
        var parserSupported = false;
        var message = "未发现可读取的 Codex 登录缓存。";
        var runtime = CodexRuntime.Inspect();
        try
        {
            var home = CodexRuntime.ResolveCodexHome();
            CodexRuntime.EnsureFileAuthStore(home);
            supported = true;
            using var snapshot = AuthSnapshot.Read(Path.Combine(home, "auth.json"));
            parserSupported = true;
            message = "本机缓存格式兼容；只读解析未验证服务端账号状态。";
        }
        catch (UserFacingException error) { message = error.Message; }
        catch { message = "只读检查失败，没有更改账号。"; }
        File.WriteAllText(Path.GetFullPath(outputFile), JsonSerializer.Serialize(new
        {
            file_store_supported = supported,
            local_auth_format_supported = parserSupported,
            client_running = !runtime.CanSwitch,
            credentials_written = false,
            message
        }, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(parserSupported && supported ? 0 : 1);
    }
}
