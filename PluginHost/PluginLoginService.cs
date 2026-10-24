using System.Diagnostics;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.PluginHost;

public sealed record PluginLoginJob(string Id, string Label, string State, string Message, DateTimeOffset ExpiresAtUtc);

public sealed class PluginLoginService
{
    private readonly VaultStore vault;
    private readonly PrivateJobStore<PluginLoginJob> jobs;
    private readonly bool allowWorkers;
    public PluginLoginService(VaultStore vault, bool allowWorkers = true)
    {
        this.vault = vault;
        this.allowWorkers = allowWorkers;
        jobs = new PrivateJobStore<PluginLoginJob>(vault.DirectoryPath, "plugin-login.dpapi");
    }

    public PluginLoginJob Begin(string label)
    {
        if (!allowWorkers) throw new UserFacingException("隔离验证模式不启动真实登录。");
        if (string.IsNullOrWhiteSpace(label) || label.Length > 60 || label.Any(char.IsControl))
            throw new UserFacingException("请提供 1 至 60 个字符的账号名称。");
        var job = jobs.Locked(old =>
        {
            if (old?.State == "waiting" && old.ExpiresAtUtc > DateTimeOffset.UtcNow)
                throw new UserFacingException("已有登录在等待完成，请先完成或取消它。");
            var created = new PluginLoginJob(Guid.NewGuid().ToString("N"), label.Trim(), "waiting", "请在官方浏览器页面手动登录目标账号。当前 Codex 登录保持不变。", DateTimeOffset.UtcNow.AddMinutes(6));
            return (created, created);
        });
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new UserFacingException("无法找到插件后台组件。")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--plugin-login");
            start.ArgumentList.Add(job.Id);
            using var child = Process.Start(start) ?? throw new UserFacingException("登录组件未能启动。");
        }
        catch
        {
            Update(job.Id, "failed", "登录组件未能启动，未更改当前账号。");
            throw new UserFacingException("登录组件未能启动。请确认插件 runtime 文件完整。");
        }
        return job;
    }

    public PluginLoginJob? Status() => jobs.Locked(old =>
    {
        if (old?.State == "waiting" && old.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            var expired = old with { State = "expired", Message = "登录等待已过期，请重新添加账号。" };
            return (expired, expired);
        }
        return ((PluginLoginJob?)null, old);
    });

    public bool Cancel() => jobs.Locked(old =>
    {
        if (old?.State != "waiting") return ((PluginLoginJob?)null, false);
        return (old with { State = "cancelled", Message = "登录已取消，正在清理临时缓存。" }, true);
    });

    public async Task<int> RunWorkerAsync(string id)
    {
        var job = Status();
        if (job?.Id != id || job.State != "waiting") return 1;
        using var stop = new CancellationTokenSource();
        var watcher = WatchAsync(id, stop);
        try
        {
            AccountSummary Commit(AuthSnapshot snapshot, string? label) => jobs.Locked(old =>
            {
                if (old?.Id != id || old.State != "waiting" || old.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new UserFacingException("添加账号已经取消或过期，未保存新账号。");
                var saved = vault.Save(snapshot, label);
                return (old with { State = "completed", Message = "新账号已加密保存，正在清理临时登录数据。" }, saved);
            });
            await new OfficialLogin(vault, Commit).AddAccountAsync(job.Label, stop.Token);
            Update(id, "completed", "新账号已加密保存；当前 Codex 登录未更改。");
            return 0;
        }
        catch (Exception error)
        {
            var old = Status();
            Update(id, old?.State == "cancelled" || stop.IsCancellationRequested ? "cancelled" : "failed", error is UserFacingException safe ? safe.Message : "登录未完成，未更改当前账号。");
            return 1;
        }
        finally { stop.Cancel(); await watcher; }
    }

    private async Task WatchAsync(string id, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(800, stop.Token);
                var current = Status();
                if (current?.Id == id && current.State == "completed") return;
                if (current?.Id != id || current.State != "waiting") stop.Cancel();
            }
            catch (OperationCanceledException) { return; }
            catch { stop.Cancel(); return; }
        }
    }

    private void Update(string id, string state, string message) => jobs.Locked(old =>
        old?.Id == id ? (old with { State = state, Message = message }, true) : ((PluginLoginJob?)null, false));
}
