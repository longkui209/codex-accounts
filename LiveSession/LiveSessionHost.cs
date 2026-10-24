using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.LiveSession;

public static class LiveSessionHost
{
    public static async Task RunAsync(CancellationToken cancellation)
    {
        // This route lacks the native app-tools binding; the user requires
        // complete compatibility, so no real startup is enabled yet.
        if (!FullNativeCompatibilityVerified())
            throw new UserFacingException("完整原生功能兼容尚未完成，直切启动暂未启用。当前 Codex 功能和登录保持原样。");
        var vault = McpServer.CreateVault();
        var state = new LiveState(vault);
        if (state.Descriptor() is not null) throw new UserFacingException("账号直切模式已经运行，请在当前 Codex 账号面板使用。");
        var runtime = CodexRuntime.Inspect();
        if (!runtime.CanSwitch) throw new UserFacingException("首次启用需正常退出所有 Codex 窗口和后台，然后从「Codex（账号直切）」打开。正在运行的任务不会被结束。");
        using var lease = state.AcquireHostLease();
        var home = CodexRuntime.ResolveCodexHome();
        CodexRuntime.EnsureFileAuthStore(home);
        // This initial encrypted import is part of the user's explicit integration request.
        using var current = AuthSnapshot.Read(Path.Combine(home, "auth.json"));
        var captured = vault.Save(current);
        var selected = state.SelectedProfile();
        if (selected is null || !vault.List().Any(account => account.Id == selected)) selected = captured.Id;
        var cli = CodexRuntime.FindCli() ?? throw new UserFacingException("未找到官方 Codex 后台程序。");
        DesktopLaunch.ValidateOfficialCli(cli);
        await VerifyVersionAsync(cli, home, cancellation);
        var instance = Guid.NewGuid().ToString("N");
        var descriptor = new LiveDescriptor(instance, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            "codex-accounts-control-" + instance, "codex-accounts-desktop-" + instance, "0.162.0-alpha.2");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Process? child = null, desktop = null;
        LiveBackend? backend = null;
        Task? desktopGateway = null, controlServer = null;
        Task? outputDrain = null, errorDrain = null;
        var capabilityBytes = RandomNumberGenerator.GetBytes(32);
        var capability = Convert.ToBase64String(capabilityBytes).TrimEnd('=').Replace('+','-').Replace('/','_');
        CryptographicOperations.ZeroMemory(capabilityBytes);
        try
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var start = StartInfo(cli, home);
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("cli_auth_credentials_store=\"ephemeral\"");
            start.ArgumentList.Add("app-server"); start.ArgumentList.Add("--listen"); start.ArgumentList.Add("ws://127.0.0.1:" + port);
            start.ArgumentList.Add("--ws-auth"); start.ArgumentList.Add("capability-token");
            start.ArgumentList.Add("--ws-token-sha256"); start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capability))).ToLowerInvariant());
            child = Process.Start(start) ?? throw new UserFacingException("直切后台未能启动。");
            outputDrain = DrainAsync(child.StandardOutput); errorDrain = DrainAsync(child.StandardError);
            for (var retry = 0; retry < 80 && !OwnedListener.IsOwned(child, port); retry++)
            {
                if (child.HasExited) throw new UserFacingException("官方后台拒绝了直切启动参数，未更改登录缓存。");
                await Task.Delay(125, lifetime.Token);
            }
            if (!OwnedListener.IsOwned(child, port)) throw new UserFacingException("不能验证本机后台身份，已拒绝发送账号凭据。");
            using var credentials = new LiveCredentials(vault);
            backend = new LiveBackend(new Uri("ws://127.0.0.1:" + port + "/"), capability, credentials, selected);
            backend.EndpointOwnershipCheck = () => OwnedListener.IsOwned(child, port);
            await backend.ConnectAsync(lifetime.Token);
            var gateway = new DesktopGateway(descriptor.DesktopPipe, new Uri("ws://127.0.0.1:" + port + "/"), capability, backend);
            desktopGateway = gateway.RunAsync(lifetime.Token);
            await gateway.Ready.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            async Task<LiveStatus> BoundStatus(CancellationToken token)
            {
                var result = await backend.StatusAsync(token);
                var connected = result.Connected && desktop is not null && !desktop.HasExited &&
                    gateway.InitializedClientProcessIds.Contains(desktop.Id);
                return result with { Connected = connected, Busy = result.Busy || !connected,
                    Message = connected ? result.Message : "正在等待 Codex 连接直切模式，暂时不能切换。" };
            }
            async Task<LiveStatus> Switch(string profile, CancellationToken token)
            {
                var before = await BoundStatus(token);
                if (!before.Connected || before.Busy) throw new UserFacingException("请等待当前任务结束，并确认直切连接正常后再切换。");
                var changed = await backend.SwitchAsync(profile, token);
                if (changed.Connected && changed.CurrentAccountId == profile) state.Select(profile);
                return changed;
            }
            var controls = new LiveControlServer(descriptor.ControlPipe, BoundStatus, Switch);
            controlServer = controls.RunAsync(lifetime.Token);
            state.Publish(descriptor);
            desktop = DesktopLaunch.Launch(descriptor.DesktopPipe);
            state.Select(selected);
            // Read-only server validation belongs to this owned instance; it does not sample a model.
            var deadline = Stopwatch.StartNew();
            while (!gateway.InitializedClientProcessIds.Contains(desktop.Id))
            {
                if (desktop.HasExited || child.HasExited || desktopGateway.IsCompleted || controlServer.IsCompleted ||
                    deadline.Elapsed > TimeSpan.FromSeconds(60))
                    throw new UserFacingException("Codex 尚未完成直切连接。普通入口和登录缓存保留；请关闭此窗口后用普通 Codex 入口恢复。");
                await Task.Delay(250, lifetime.Token);
            }
            while (!desktop.HasExited && !child.HasExited)
                await Task.Delay(750, lifetime.Token);
            lifetime.Cancel();
        }
        finally
        {
            lifetime.Cancel();
            state.RemoveOwned(instance);
            if (desktopGateway is not null) try { await desktopGateway; } catch { }
            if (controlServer is not null) try { await controlServer; } catch { }
            if (backend is not null) await backend.DisposeAsync();
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree:false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
                child.Dispose();
            }
            if (outputDrain is not null) try { await outputDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (errorDrain is not null) try { await errorDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            desktop?.Dispose(); // Never terminate a desktop window or its tasks.
        }
    }
    private static bool FullNativeCompatibilityVerified() => false;
    private static ProcessStartInfo StartInfo(string executable, string home)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true,
            RedirectStandardError=true, RedirectStandardOutput=true, WorkingDirectory=home };
        start.Environment["CODEX_HOME"]=home;
        foreach (var key in new[]{"OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_FEDERATION_RULE_ID","OPENAI_IDENTITY_TOKEN_FILE","OPENAI_WORKLOAD_IDENTITY_CONTEXT","CODEX_APP_TOOLS_PIPE_PATH","CODEX_THREAD_ID","CODEX_SESSION_ID"})
            start.Environment.Remove(key);
        start.Environment.TryGetValue("NO_PROXY", out var bypass);
        start.Environment["NO_PROXY"]=string.Join(",",new[]{bypass,"127.0.0.1","localhost"}.Where(value=>!string.IsNullOrEmpty(value)));
        return start;
    }
    private static async Task VerifyVersionAsync(string cli, string home, CancellationToken cancellation)
    {
        var start = StartInfo(cli,home); start.ArgumentList.Add("--version");
        using var process=Process.Start(start) ?? throw new UserFacingException("不能验证官方后台版本。");
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(TimeSpan.FromSeconds(5));
        var text=process.StandardOutput.ReadToEndAsync(limit.Token); var error=DrainAsync(process.StandardError);
        try { await process.WaitForExitAsync(limit.Token); }
        catch { try{ if(!process.HasExited)process.Kill(false); }catch{} throw new UserFacingException("后台版本验证超时。"); }
        await error;
        if(process.ExitCode!=0 || (await text).Trim()!="codex-cli 0.162.0-alpha.2")
            throw new UserFacingException("此直切接入目前只支持已核验的 Codex 0.162.0-alpha.2，请保留普通入口。");
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync() is not null) { } }
        catch { }
    }
}
