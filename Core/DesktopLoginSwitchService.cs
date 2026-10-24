using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.PluginHost;

namespace CodexAccountSwitcher.Core;

public sealed record DesktopLoginSwitchRequest(string Id,string AccountId,string ExpectedDesktopId,string Label,string MaskedEmail,
    string State,string Message,DateTimeOffset CreatedAtUtc,DateTimeOffset ExpiresAtUtc);

/// <summary>A user-confirmed intent applied by the next owned startup, never by an orphaned exit-wait worker.</summary>
public sealed class DesktopLoginSwitchService
{
    private readonly VaultStore vault;
    private readonly string home;
    private readonly Func<string,CancellationToken,Task<AccountUsage>>? checkUsage;
    private readonly Func<RuntimeStatus> guard;
    private readonly TimeProvider clock;
    private readonly PrivateJobStore<DesktopLoginSwitchRequest> jobs;
    public DesktopLoginSwitchService(VaultStore vault,string home,Func<string,CancellationToken,Task<AccountUsage>>? checkUsage=null,
        Func<RuntimeStatus>? guard=null,TimeProvider? clock=null)
    {
        this.vault=vault;this.home=AuthFileSafety.NormalizeDirectory(home);this.checkUsage=checkUsage;
        this.guard=guard ?? CodexRuntime.Inspect;this.clock=clock ?? TimeProvider.System;
        jobs=new(vault.DirectoryPath,"desktop-login-switch.dpapi");
    }
    public DesktopLoginSwitchRequest? Status()=>jobs.Locked<DesktopLoginSwitchRequest?>(value=>
    {
        if(value is null)return((DesktopLoginSwitchRequest?)null,null);
        Validate(value);
        if(IsActive(value) && value.ExpiresAtUtc<=clock.GetUtcNow())
        {
            var expired=value with {State="expired",Message=value.State=="applying"
                ? "桌面登录切换的恢复确认已过期；缓存可能已经改变，请核对当前登录及加密备份。未确认原生登录或发送成功。"
                : "桌面登录切换确认已过期，没有自动改动登录。请重新选择。"};
            return(expired,expired);
        }
        return((DesktopLoginSwitchRequest?)null,value);
    });
    internal static bool IsActive(DesktopLoginSwitchRequest value)=>value.State is "queued" or "applying";
    internal TResult WhileIdle<TResult>(Func<TResult> action)=>jobs.Locked(value=>
    {
        if(value is not null){Validate(value);if(IsActive(value) && value.ExpiresAtUtc>clock.GetUtcNow())
            throw new UserFacingException("请先完成或取消同步切换桌面登录，再操作账号。");}
        return((DesktopLoginSwitchRequest?)null,action());
    });
    public async Task<DesktopLoginSwitchRequest> RequestAsync(string accountId,bool confirmed,CancellationToken cancellation)
    {
        if(!confirmed || !LiveState.ValidId(accountId))throw new UserFacingException("必须先确认具体账号及桌面登录切换的影响。");
        var account=vault.List().SingleOrDefault(entry=>entry.Id==accountId) ?? throw new UserFacingException("该账号已不在保存列表中。");
        var expected=CurrentDesktopId();
        if(accountId==expected)throw new UserFacingException("该账号已是桌面登录身份，未重复登记切换。");
        AssertNoOtherOperation();
        await AssertUsableAsync(accountId,cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if(CurrentDesktopId()!=expected)throw new UserFacingException("查询期间桌面登录已变化，请重新确认。");
        return jobs.Locked(old=>
        {
            AssertNoOtherOperation();
            if(old is not null){Validate(old);if(IsActive(old) && old.ExpiresAtUtc>clock.GetUtcNow())throw new UserFacingException("已有桌面登录切换待完成，请先取消或正常重开。");}
            var now=clock.GetUtcNow();
            var request=new DesktopLoginSwitchRequest(Guid.NewGuid().ToString("N"),accountId,expected,account.Label,account.MaskedEmail,"queued",
                "已登记。请保存任务，正常退出全部 ChatGPT/Codex 窗口，再从「Codex（订阅切换）」打开；下次启动时才会应用。有效期 15 分钟。",
                now,now.AddMinutes(15));
            return(request,request);
        });
    }
    public bool Cancel(string id)=>jobs.Locked(value=>
    {
        if(value is null || value.Id!=id || value.State!="queued")return((DesktopLoginSwitchRequest?)null,false);
        return(value with {State="cancelled",Message="桌面登录切换已取消，当前登录未改变。"},true);
    });
    public async Task<DesktopLoginSwitchRequest?> ApplyAtStartupAsync(CancellationToken cancellation)
    {
        var request=Status();
        if(request is null || !IsActive(request))return request;
        try
        {
            AssertClosed();AssertNoOtherOperation();
            var current=CurrentDesktopId();
            var recovering=request.State=="applying" && current==request.AccountId;
            if(!recovering && current!=request.ExpectedDesktopId)throw new UserFacingException("桌面登录已变化，未覆盖新登录；请重新确认切换。");
            await AssertUsableAsync(request.AccountId,cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            jobs.Locked(latest=>
            {
                if(latest?.Id!=request.Id || !IsActive(latest) || latest.ExpiresAtUtc<=clock.GetUtcNow())
                    throw new UserFacingException("切换已取消、过期或记录发生变化，未应用旧请求。");
                return(latest with {State="applying",Message="正在核对并应用已确认的桌面登录切换。"},true);
            });
            return jobs.Locked(latest=>
            {
                if(latest is null || latest.Id!=request.Id || !IsActive(latest))throw new UserFacingException("切换已取消或记录发生变化，未应用旧请求。");
                if(latest.ExpiresAtUtc<=clock.GetUtcNow())throw new UserFacingException("桌面登录切换确认已过期，未应用。");
                cancellation.ThrowIfCancellationRequested();
                AssertClosed();
                if(!recovering)new AccountSwitchService(vault,home,guard).Switch(latest.AccountId,latest.ExpectedDesktopId);
                else if(CurrentDesktopId()!=latest.AccountId)throw new UserFacingException("恢复检查期间登录发生变化，未继续。");
                new LiveState(vault,"routing").Select(latest.AccountId);
                var completed=latest with {State="completed",Message="已写入桌面登录缓存并选择同一模型订阅。原生登录及发送按钮仍以重新打开后的实际结果为准。"};
                return(completed,completed);
            });
        }
        catch
        {
            var cacheIsTarget=false;try{cacheIsTarget=CurrentDesktopId()==request.AccountId;}catch{}
            try{jobs.Locked(value=>value?.Id==request.Id && IsActive(value)
                ? (value with {State=cacheIsTarget?"applying":"failed",Message=cacheIsTarget ? "目标登录缓存已存在，后续启动步骤待恢复核对；尚未确认原生登录。" : "桌面登录切换未完成，未确认新账号生效。请查看当前登录及加密备份；不会自动选择其他账号。"},true)
                : (null,false));}catch{}
            throw;
        }
    }
    private async Task AssertUsableAsync(string id,CancellationToken cancellation)
    {
        if(checkUsage is null)throw new UserFacingException("此验证环境不查询真实账号，未登记或应用桌面登录切换。");
        var usage=await checkUsage(id,cancellation).ConfigureAwait(false);
        var now=clock.GetUtcNow();
        if(usage.Status!="ok" || usage.Stale || usage.UpdatedAtUtc is null || usage.UsageAllowed!=true ||
            now-usage.UpdatedAtUtc>TimeSpan.FromMinutes(2) || usage.UpdatedAtUtc>now.AddSeconds(30))
            throw new UserFacingException("目标账号当前额度或登录未通过官方核对，未切换桌面登录。");
    }
    private void AssertClosed()
    {
        var status=guard();if(!status.CanSwitch)throw new UserFacingException("仍有客户端或后台在运行，未改动桌面登录。请正常退出后再打开专用入口。");
    }
    private string CurrentDesktopId()
    {
        CodexRuntime.EnsureFileAuthStore(home);
        using var snapshot=AuthSnapshot.Read(Path.Combine(home,"auth.json"));return snapshot.Info.IdentityKey;
    }
    private void AssertNoOtherOperation()
    {
        if(new DeferredSwitchService(vault,home).Status()?.State is "pending" or "waiting" or "switching" ||
            new PluginLoginService(vault).Status()?.State=="waiting")throw new UserFacingException("请先完成或取消其他账号操作。");
        foreach(var account in vault.List())
        {
            var reset=new PrivateJobStore<ResetAttempt>(Path.Combine(vault.DirectoryPath,"reset-actions"),account.Id+".dpapi")
                .Locked(value=>((ResetAttempt?)null,value));
            if(reset?.State is "sending" or "unknown")throw new UserFacingException("有重置卡操作尚未确认，请先核对后再切换桌面登录。");
        }
    }
    private static void Validate(DesktopLoginSwitchRequest value)
    {
        if(!Guid.TryParseExact(value.Id,"N",out _) || !LiveState.ValidId(value.AccountId) || !LiveState.ValidId(value.ExpectedDesktopId) ||
            value.AccountId==value.ExpectedDesktopId || value.Label is not {Length:>0 and <=80} || value.Label.Any(char.IsControl) ||
            value.State is not ("queued" or "applying" or "completed" or "cancelled" or "expired" or "failed") ||
            value.ExpiresAtUtc<=value.CreatedAtUtc || value.ExpiresAtUtc-value.CreatedAtUtc>TimeSpan.FromMinutes(15))
            throw new UserFacingException("桌面登录切换记录无效，未应用。");
    }
}
