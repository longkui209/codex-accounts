using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

internal sealed record InferenceGateState(int Version,bool Connected,bool Allowed,long ExpiresAtMs);
public sealed record NativeInputStatus(bool Supported,bool Connected,bool FrontendAdapted,bool QuotaAvailable,string State,DateTimeOffset? UpdatedAtUtc)
{
    public bool InitialQuotaAdapted {get;init;}
    public bool WorkQuotaAdapted {get;init;}
    public bool? WorkQuotaNativeBlocked {get;init;}
    public bool? WorkQuotaEffectiveBlocked {get;init;}
    public DateTimeOffset? WorkQuotaCheckedAtUtc {get;init;}
}

/// <summary>The desktop identity's usage stays untouched; this state describes only the actual inference route.</summary>
internal sealed class InferenceGateService(VaultStore vault,RoutingController controller,AccountUsageService usage)
{
    internal async Task<InferenceGateState> ReadAsync(CancellationToken cancellation)
    {
        var status=await controller.StatusAsync(cancellation).ConfigureAwait(false);
        if(!status.Connected || !status.NativeConnectionObserved || status.CurrentAccountId is not { } id)
            return Denied();
        var candidate=await usage.RefreshAsync(id,false,cancellation).ConfigureAwait(false);
        var allowed=IsUsable(candidate,DateTimeOffset.UtcNow);
        if(!allowed && status.AutoRotationEnabled)
        {
            var others=vault.List().Select(account=>account.Id).Where(account=>account!=id).ToArray();
            await usage.RefreshAllAsync(others,false,cancellation).ConfigureAwait(false);
            allowed=others.Any(other=>IsUsable(usage.Cached(other),DateTimeOffset.UtcNow));
        }
        var current=await controller.StatusAsync(cancellation).ConfigureAwait(false);
        if(!current.Connected || !current.NativeConnectionObserved || current.CurrentAccountId!=id ||
            current.AutoRotationEnabled!=status.AutoRotationEnabled)return Denied();
        return new(1,true,allowed,DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds());
    }
    internal static bool IsUsable(AccountUsage? value,DateTimeOffset now)=>value is {Status:"ok",Stale:false,UsageAllowed:true,UpdatedAtUtc:{ } updated}
        && updated<=now.AddSeconds(30) && now-updated<=TimeSpan.FromSeconds(90);
    internal static InferenceGateState Denied()=>new(1,false,false,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}
