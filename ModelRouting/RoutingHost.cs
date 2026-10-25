using System.Diagnostics;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.ModelRouting;

public static class RoutingHost
{
    public static Task RunAsync(CancellationToken cancellation)=>RunCoreAsync(cancellation,null,Array.Empty<string>());
    internal static Task RunIntegratedAsync(CancellationToken cancellation,NativeRuntimeLease runtime,IReadOnlyList<string> arguments)=>RunCoreAsync(cancellation,runtime,arguments);
    private static async Task RunCoreAsync(CancellationToken cancellation,NativeRuntimeLease? integratedRuntime,IReadOnlyList<string> arguments)
    {
        var vault = McpServer.CreateVault();
        var startup = new RoutingStartupStore(vault);
        var stage = "preflight";
        startup.Record(stage);
        try
        {
        // A running Electron singleton would keep its previous environment. Never terminate it.
        var runtime = CodexRuntime.Inspect();
        if (!runtime.CanSwitch) throw new UserFacingException("请先结束任务并正常退出所有 Codex 桌面和后台，再从「Codex（订阅切换）」打开。不会关闭正在运行的窗口。");
        var home = CodexRuntime.ResolveCodexHome();
        CodexRuntime.EnsureFileAuthStore(home);
        RoutingSettingsGuard.AssertNoExistingEndpoint(home);
        var state = new LiveState(vault, "routing");
        using var hostLease = state.AcquireHostLease();
        stage = "credentials";
        startup.Record(stage);
        using var saved = new LiveCredentials(vault);
        using var startupUsage=new AccountUsageService(vault,async(id,token)=>
        {
            using var current=AuthSnapshot.Read(Path.Combine(home,"auth.json"));
            return await new RouterCredentials(vault,home,saved,current.Info.IdentityKey).GetAsync(id,token).ConfigureAwait(false);
        });
        await new DesktopLoginSwitchService(vault,home,(id,token)=>startupUsage.RefreshAsync(id,true,token))
            .ApplyAtStartupAsync(cancellation).ConfigureAwait(false);
        string desktopId;
        using (var directoryLease = AuthFileSafety.ProtectDirectories(home))
        using (var auth = AuthSnapshot.Read(Path.Combine(home, "auth.json")))
        {
            desktopId = auth.Info.IdentityKey;
            vault.Save(auth); // Explicitly authorized, encrypted import of the latest desktop login.
        }
        var credentials = new RouterCredentials(vault, home, saved, desktopId);
        var selected = state.SelectedProfile() ?? desktopId;
        _ = await credentials.GetAsync(selected, cancellation).ConfigureAwait(false);
        state.Select(selected);
        using var rotationUsage=new AccountUsageService(vault,credentials.GetAsync);
        using var rotationCandidates=new RotationCandidates(vault,credentials.GetAsync,rotationUsage);
        var controller = new RoutingController(credentials, state, selected, requireNativeBinding: true, new RotationSettingsStore(vault));
        controller.RotationCandidate=rotationCandidates.NextAsync;
        stage = "desktop-launch";
        startup.Record(stage);
        var desktopExecutable = integratedRuntime?.Executable??DesktopLaunch.FindInstalledDesktop();
        var cliExecutable = CodexRuntime.FindCli() ?? throw new UserFacingException("未找到官方后台程序，没有接入订阅切换。");
        DesktopLaunch.ValidateOfficialCli(cliExecutable);
        stage = "listener";
        startup.Record(stage);
        await using var relay = new ModelRelay(credentials, controller);
        NativeDesktopLifetime? lifetime = null;
        relay.FrontendProcessAllowed = pid => lifetime?.IsNativeBackendCaller(pid) == true;
        var connectionRecorded = 0;
        var boundCallers = new Dictionary<int, long>();
        var bindingGate = new object();
        controller.NativeBindingCurrent = () =>
        {
            lock (bindingGate)
            {
                foreach (var caller in boundCallers.ToArray())
                {
                    var valid = false;
                    try
                    {
                        using var process = Process.GetProcessById(caller.Key);
                        valid = process.StartTime.ToUniversalTime().Ticks == caller.Value && lifetime?.IsNativeBackendCaller(caller.Key) == true;
                    }
                    catch { }
                    if (!valid) boundCallers.Remove(caller.Key);
                }
                return boundCallers.Count != 0;
            }
        };
        relay.NativeConnectionConfirmed = pid =>
        {
            using var process = Process.GetProcessById(pid);
            lock (bindingGate)
            {
                if (boundCallers.Count < 16 || boundCallers.ContainsKey(pid))
                    boundCallers[pid] = process.StartTime.ToUniversalTime().Ticks;
            }
            if (Interlocked.CompareExchange(ref connectionRecorded, 1, 0) == 0) startup.Record("native-connected");
        };
        await relay.StartAsync(cancellation).ConfigureAwait(false);
        var instance = Guid.NewGuid().ToString("N");
        using var owner = Process.GetCurrentProcess();
        var descriptor = new LiveDescriptor(instance, owner.Id, owner.StartTime.ToUniversalTime().Ticks,
            "codex-accounts-control-" + instance, "codex-accounts-desktop-" + instance, "native-subscription-routing");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        controller.RemoveSavedAccount = id => new AccountRemovalService(vault,home).RemoveInactive(id);
        var control = new LiveControlServer(descriptor.ControlPipe, controller.StatusAsync, controller.SwitchAsync, controller.RemoveAsync, controller.ConfigureRotationAsync).RunAsync(stop.Token);
        try
        {
            state.Publish(descriptor);
            stage = "desktop-launch";
            startup.Record(stage);
            var extraCli=integratedRuntime is null?Array.Empty<string>():new[]{integratedRuntime.BundledCli};
            foreach(var extra in extraCli)DesktopLaunch.ValidateOfficialCli(extra);
            lifetime = new NativeDesktopLifetime(desktopExecutable, cliExecutable, DateTimeOffset.UtcNow,extraCli);
            await using var surface=new NativeSurfacePipe();
            var gateService=new InferenceGateService(vault,controller,rotationUsage);
            await using var nativeInput=new NativeInputBridge(surface,gateService.ReadAsync);
            try{nativeInput.Prepare();} // Discover before script requests; unknown resources retain native behavior.
            catch{ /* StartAsync records incompatibility; desktop launch itself must remain available. */ }
            controller.NativeInputStatus=()=>nativeInput.Status;
            var start=NativeRoutingLaunch.BuildStartInfo(desktopExecutable,relay.BaseUri!);
            start.Environment.TryGetValue("CODEX_ELECTRON_USER_DATA_PATH",out var configuredProfile);
            var profile=NativeBrowserProfile.Resolve(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),configuredProfile);
            if(integratedRuntime is null)surface.Start(start,profile);
            else surface.StartWithRuntime(start,profile,integratedRuntime,arguments);
            var desktop=surface.Desktop ?? throw new IOException();
            try{await nativeInput.StartAsync(cancellation).ConfigureAwait(false);}
            catch{ /* The owned desktop and relay remain usable with native quota checks; status never claims the adapter loaded. */ }
            stage = "awaiting-native";
            if (Volatile.Read(ref connectionRecorded) == 0) startup.Record(stage);
            // The desktop owns the official stdio/Core process and every native tool binding.
            // A control-server fault must not terminate the desktop or its active model requests.
            await lifetime.TrackUntilClosedAsync(desktop, TimeSpan.FromSeconds(5), () => controller.HasActiveRequests, cancellation).ConfigureAwait(false);
            stage = "closed";
            startup.Record(stage);
        }
        finally
        {
            state.RemoveOwned(instance);
            stop.Cancel();
            try { await control.ConfigureAwait(false); } catch { }
        }
        }
        catch
        {
            try { startup.Record(stage, failed: true); } catch { }
            throw;
        }
    }
}
