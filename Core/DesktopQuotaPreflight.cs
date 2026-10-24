namespace CodexAccountSwitcher.Core;

public sealed record DesktopQuotaPreflight(string State, bool? Allowed, bool DifferentInferenceAccount,
    DateTimeOffset? UpdatedAtUtc, string Message)
{
    public static DesktopQuotaPreflight Check(AccountUsage? usage,bool differentInferenceAccount)
    {
        if(usage is null || usage.UpdatedAtUtc is null)
            return new("unknown",null,differentInferenceAccount,null,"尚未查询桌面登录账号的额度；模型订阅选择不能证明原生发送按钮可用。");
        if(usage.Stale || usage.Status!="ok")
            return new("stale",null,differentInferenceAccount,usage.UpdatedAtUtc,"桌面登录额度数据待更新；请刷新核对，不能依据旧缓存判断发送按钮。");
        if(usage.UsageAllowed==false)
            return new("desktop-exhausted",false,differentInferenceAccount,usage.UpdatedAtUtc,
                differentInferenceAccount ? "桌面登录账号额度已耗尽。原生发送按钮会在请求进入转发层前受限，单独切换模型订阅不能解除此限制。"
                    : "桌面登录账号额度已耗尽，原生发送按钮可能禁用；需要使用有额度的账号登录桌面。");
        if(usage.UsageAllowed==true)
            return new("allowed",true,differentInferenceAccount,usage.UpdatedAtUtc,"官方额度查询允许使用；客户端发送按钮是否恢复仍需实际确认。");
        return new("unknown",null,differentInferenceAccount,usage.UpdatedAtUtc,"官方尚未提供桌面登录额度的允许状态，不能按用量百分比猜测。");
    }
}
