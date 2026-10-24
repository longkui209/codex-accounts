using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.Validation;

public static class DesktopQuotaPreflightSelfTests
{
    public static IReadOnlyList<string> Run()
    {
        var now=DateTimeOffset.UtcNow;
        var exhausted=new AccountUsage("ok","Plus",new(100,18000,now.AddHours(1)),null,null,now,now,"Synthetic only") {UsageAllowed=false};
        var blocked=DesktopQuotaPreflight.Check(exhausted,true);
        Require(blocked.State=="desktop-exhausted" && blocked.Allowed==false && blocked.Message.Contains("单独切换模型订阅不能解除"),"推理订阅变化不能假装解除原生发送拦截");
        Require(DesktopQuotaPreflight.Check(exhausted with {Stale=true},true).State=="stale","旧的 100% 不能冒充当前仍耗尽");
        Require(DesktopQuotaPreflight.Check(exhausted with {Status="unavailable"},true).Allowed is null,"官方查询失败不能猜测允许状态");
        Require(DesktopQuotaPreflight.Check(exhausted with {UsageAllowed=true},true).State=="allowed","官方允许优先于显示百分比");
        Require(DesktopQuotaPreflight.Check(exhausted with {UsageAllowed=null},false).State=="unknown","百分比不能代替官方允许布尔值");
        Require(DesktopQuotaPreflight.Check(null,true).Allowed is null,"从未查询时保持未知");
        return ["PASS: 桌面额度前置状态与推理切换分离，旧 100% 标记过期，不把选择或百分比当作原生发送已恢复。"]; 
    }
    private static void Require(bool condition,string name)
    {if(!condition)throw new UserFacingException("隔离桌面额度检查失败："+name+"。");}
}
