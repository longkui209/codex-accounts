using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Core;

public sealed class AccountRemovalService(VaultStore vault,string home)
{
    public RemovedAccountSummary RemoveInactive(string id)
        =>new DesktopLoginSwitchService(vault,home).WhileIdle(()=>RemoveUnreserved(id));
    private RemovedAccountSummary RemoveUnreserved(string id)
    {
        if (!LiveState.ValidId(id)) throw new UserFacingException("账号标识无效。");
        var pending = new DeferredSwitchService(vault,home).Status();
        if (pending?.State is "pending" or "waiting" or "switching" || new PluginLoginService(vault).Status()?.State == "waiting")
            throw new UserFacingException("请先完成或取消正在进行的账号操作，再移除保存记录。");
        // The reset state lock precedes the vault lock, matching the redemption reservation order.
        // Never hold the vault while reading login/deferred jobs: their workers save in the reverse order.
        return new PrivateJobStore<ResetAttempt>(Path.Combine(vault.DirectoryPath,"reset-actions"),id+".dpapi")
            .Locked(value =>
            {
                if (value?.State is "sending" or "unknown") throw new UserFacingException("该账号有尚未确认的重置卡操作，请先核对结果。");
                return ((ResetAttempt?)null,vault.Remove(id));
            });
    }
    public RemovedAccountSummary RemoveOffline(string id)
    {
        if (!LiveState.ValidId(id)) throw new UserFacingException("账号标识无效。");
        // Excludes a host starting between the offline check and the vault mutation.
        using var routingLease = new LiveState(vault,"routing").AcquireHostLease();
        using var liveLease = new LiveState(vault).AcquireHostLease();
        if (File.Exists(Path.Combine(home,"auth.json")))
        {
            using var desktop = AuthSnapshot.Read(Path.Combine(home,"auth.json"));
            if (desktop.Info.IdentityKey == id) throw new UserFacingException("请先切换当前使用的账号，再移除其保存记录。");
        }
        if (new LiveState(vault,"routing").SelectedProfile() == id || new LiveState(vault).SelectedProfile() == id)
            throw new UserFacingException("请先选择另一个订阅账号，再移除这条保存记录。");
        return RemoveInactive(id);
    }
}
