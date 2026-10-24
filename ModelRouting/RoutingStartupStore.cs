using System.Diagnostics;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.ModelRouting;

public sealed record RoutingStartupStatus(string Stage, string State, string Message, DateTimeOffset UpdatedAtUtc);
internal sealed record RoutingStartupRecord(RoutingStartupStatus Status, int OwnerPid, long OwnerStartTicks);

public sealed class RoutingStartupStore
{
    private readonly PrivateJobStore<RoutingStartupRecord> records;
    private static readonly HashSet<string> Stages = new(StringComparer.Ordinal)
        { "preflight", "credentials", "listener", "desktop-launch", "awaiting-native", "native-connected", "closed" };
    public RoutingStartupStore(VaultStore vault) => records = new(Path.Combine(vault.DirectoryPath, "routing"), "startup.dpapi");
    public void Record(string stage, bool failed = false)
    {
        if (!Stages.Contains(stage)) throw new ArgumentException("Unsupported startup stage.");
        using var owner = Process.GetCurrentProcess();
        var status = new RoutingStartupStatus(stage, failed ? "failed" : stage == "closed" ? "closed" : stage == "native-connected" ? "connected" : "starting",
            Message(stage, failed), DateTimeOffset.UtcNow);
        records.Locked(_ => { var record = new RoutingStartupRecord(status, owner.Id, owner.StartTime.ToUniversalTime().Ticks); return (record, true); });
    }
    public RoutingStartupStatus? Status() => records.Locked(record =>
    {
        if (record is null) return ((RoutingStartupRecord?)null, (RoutingStartupStatus?)null);
        var status = record.Status;
        if (!Stages.Contains(status.Stage) || status.State is not ("starting" or "connected" or "failed" or "closed"))
            throw new UserFacingException("订阅启动记录无法验证，不能确认接入。");
        if (status.State is "starting" or "connected")
        {
            var alive = false;
            try
            {
                using var owner = Process.GetProcessById(record.OwnerPid);
                alive = !owner.HasExited && owner.ProcessName.Equals("CodexAccountSwitcher", StringComparison.OrdinalIgnoreCase) &&
                    owner.StartTime.ToUniversalTime().Ticks == record.OwnerStartTicks;
            }
            catch { }
            if (!alive) status = status with { State = "failed", Message = "订阅启动程序已退出，转发没有连接。账号保存或选择记录不能证明切换成功。" };
        }
        return ((RoutingStartupRecord?)null, (RoutingStartupStatus?)status);
    });
    private static string Message(string stage, bool failed)
    {
        if (failed) return stage switch
        {
            "preflight" => "订阅入口启动检查未通过，当前窗口和登录保留。请结束任务并正常退出所有 Codex 后，再使用「Codex（订阅切换）」打开。",
            "credentials" => "订阅凭据检查未通过，没有切换。请重新通过官方登录保存所选账号。",
            "listener" => "本机转发服务没有成功启动，没有切换。",
            "desktop-launch" => "官方桌面启动未通过签名或环境检查，没有切换。普通入口和登录保留。",
            _ => "未能确认官方桌面的有效连接，不能宣称订阅切换成功。"
        };
        return stage switch
        {
            "preflight" => "正在检查原生桌面启动条件，尚未连接。",
            "credentials" => "正在检查账号库与凭据，尚未切换。",
            "listener" => "正在启动本机转发服务，尚未连接桌面。",
            "desktop-launch" => "正在启动签名官方桌面，尚未收到有效请求。",
            "awaiting-native" => "等待官方桌面的有效连接；服务已启动不等于订阅切换成功。",
            "native-connected" => "已收到官方桌面的有效请求；账号选择仍需实际模型请求完成才能确认生效。",
            _ => "订阅入口已正常关闭，下次需从「Codex（订阅切换）」打开。"
        };
    }
}
