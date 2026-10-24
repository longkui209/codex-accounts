using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Selection affects the next request. An active request owns an immutable credential snapshot.</summary>
public sealed class RoutingController(RouterCredentials credentials, LiveState state, string initialProfile, bool requireNativeBinding = false,
    RotationSettingsStore? rotation = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string selected = initialProfile;
    private int active;
    private bool ready;
    private bool nativeObserved;
    private string? lastUsed;
    private string? lastCompleted;
    private RoutingFailure? failure;
    private RoutingResponseEvidence? lastResponse;
    internal Func<bool>? NativeBindingCurrent { get; set; }
    internal Action<string>? RemoveSavedAccount { get; set; }
    internal Func<string,string?,IReadOnlySet<string>,CancellationToken,Task<string?>>? RotationCandidate { get; set; }
    internal Func<NativeInputStatus>? NativeInputStatus {get;set;}
    private AutoRotationEvent? lastAutoRotation;
    private readonly Dictionary<string,DateTimeOffset> quotaCooldowns = new(StringComparer.Ordinal);
    private bool RotationEnabled => rotation?.Read().Enabled == true && RotationCandidate is not null;

    public async Task SetReadyAsync(bool value)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { ready = value; } finally { gate.Release(); }
    }
    public async Task ObserveNativeConnectionAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            nativeObserved = true;
            if (failure?.Code == "native-session-ended") failure = null;
        }
        finally { gate.Release(); }
    }
    public bool HasActiveRequests => Volatile.Read(ref active) > 0;
    public async Task RecordFailureAsync(string code, int? httpStatus = null)
    {
        // Fixed categories only. No request text, headers, upstream body or exception details.
        var message = code switch
        {
            "local-source" => "连接来源不是本机回环地址，请使用官方桌面。",
            "local-host" => "本机连接地址不匹配，未转发请求。",
            "browser-origin" => "浏览器来源请求被拒绝，未转发请求。",
            "local-peer" => "无法确认本机连接所属用户和进程，未转发请求。",
            "native-caller" => "连接进程未通过官方后台绑定校验，未转发请求。",
            "native-session-ended" => "已核验的后台已退出或重新启动，原连接已失效。请正常退出 Codex，再从「Codex（订阅切换）」打开。",
            "desktop-auth" => "桌面登录凭据与本机连接不一致，未转发请求。",
            "selected-rejected" => "所选账号被官方认证拒绝。连接空闲后可选择另一个账号，或重新通过官方登录添加该账号。桌面登录未改动。",
            "upstream-rejected" => "官方服务拒绝了本次请求，尚未确认所选订阅生效。",
            "transport-failed" => "本次转发未完成，没有自动更换账号或重放请求。",
            "quota-exhausted" => "当前账号额度已耗尽，尚未找到有额度且支持原模型的其他保存账号；没有使用重置卡。",
            _ => throw new ArgumentException("Unsupported routing failure category.")
        };
        if (httpStatus is not null && httpStatus is not (>= 100 and <= 599))
            throw new ArgumentOutOfRangeException(nameof(httpStatus));
        await gate.WaitAsync().ConfigureAwait(false);
        try { failure = new RoutingFailure(code, message, httpStatus, DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public async Task<LiveStatus> StatusAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try { return Status("已选择模型请求使用的订阅账号；桌面登录和插件连接保留。实际生效以请求结果为准。"); }
        finally { gate.Release(); }
    }
    public async Task<LiveStatus> SwitchAsync(string profileId, CancellationToken cancellation)
    {
        if (!LiveState.ValidId(profileId)) throw new UserFacingException("账号标识无效。");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!ready || requireNativeBinding && !HasCurrentNativeBinding()) throw new UserFacingException("尚未收到官方桌面的有效连接，未切换订阅。请先确认启动状态后重试。");
            if (active != 0) throw new UserFacingException("有模型请求正在执行，请等当前响应结束后切换。");
            _ = await credentials.GetAsync(profileId, cancellation).ConfigureAwait(false);
            state.Select(profileId);
            selected = profileId;
            quotaCooldowns.Remove(profileId);
            failure = null;
            lastUsed = null;
            lastCompleted = null;
            lastResponse = null;
            lastAutoRotation = null;
            return Status("已选择该订阅账号，下一次模型请求将使用它；尚未发起请求验证，不会切换桌面登录。");
        }
        finally { gate.Release(); }
    }
    public async Task<RequestLease> BeginAsync(bool desktopAccount, CancellationToken cancellation, bool completionRelevant = true)
    {
        string profileId;
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!ready) throw new UserFacingException("订阅转发服务未就绪。");
            profileId = desktopAccount ? credentials.DesktopProfileId : selected;
            if (!desktopAccount && completionRelevant) failure = null;
            active++;
        }
        finally { gate.Release(); }
        try
        {
            var credential = await credentials.GetAsync(profileId, cancellation).ConfigureAwait(false);
            return new RequestLease(this, profileId, credential, !desktopAccount && completionRelevant)
                { RotationAllowed = !desktopAccount && completionRelevant && RotationEnabled };
        }
        catch { await FinishAsync(profileId, false, false, !desktopAccount && completionRelevant, null).ConfigureAwait(false); throw; }
    }
    public async Task<LiveStatus> RemoveAsync(string profileId,CancellationToken cancellation)
    {
        if (!LiveState.ValidId(profileId)) throw new UserFacingException("账号标识无效。");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!ready || requireNativeBinding && !HasCurrentNativeBinding() || RemoveSavedAccount is null)
                throw new UserFacingException("账号连接尚未核验，暂时不能移除。");
            if (active != 0) throw new UserFacingException("当前有请求在执行，请在响应结束后移除账号。");
            if (selected == profileId) throw new UserFacingException("请先切换到另一个订阅，再移除当前账号。");
            if (credentials.DesktopProfileId == profileId) throw new UserFacingException("该账号仍是桌面当前登录，暂时不能移除它的保存记录。");
            RemoveSavedAccount(profileId);
            return Status("已从本机保存列表移除，可撤销；当前订阅与桌面登录保持原样。");
        }
        finally { gate.Release(); }
    }
    public async Task<LiveStatus> ConfigureRotationAsync(bool enabled,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (rotation is null || RotationCandidate is null) throw new UserFacingException("当前主机不支持自动轮换，请正常重开更新后的专用入口。");
            rotation.Set(enabled);
            return Status(enabled ? "额度耗尽自动轮换已开启；在转发层接续同一请求。" : "额度耗尽自动轮换已关闭。");
        }
        finally {gate.Release();}
    }
    public async Task<bool> TryRotateAsync(RequestLease lease,string? model,IReadOnlySet<string> visited,DateTimeOffset? resetsAt,
        CancellationToken cancellation)
    {
        HashSet<string> excluded;
        var from=lease.ProfileId;
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!lease.RotationAllowed || lease.Accepted || !RotationEnabled || !ready ||
                requireNativeBinding && !HasCurrentNativeBinding()) return false;
            var now=DateTimeOffset.UtcNow;
            quotaCooldowns[from]=resetsAt is { } reset && reset>now && reset<now.AddDays(35) ? reset : now.AddMinutes(1);
            excluded=new HashSet<string>(visited,StringComparer.Ordinal);
            excluded.UnionWith(quotaCooldowns.Where(entry=>entry.Value>now).Select(entry=>entry.Key));
        }
        finally {gate.Release();}
        string? candidate;
        LiveCredential credential;
        try
        {
            candidate=await RotationCandidate!(from,model,excluded,cancellation).ConfigureAwait(false);
            if(candidate is null || excluded.Contains(candidate) || !LiveState.ValidId(candidate))return false;
            credential=await credentials.GetAsync(candidate,cancellation).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){return false;}
        catch{return false;}
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            // A different concurrent request may already have rotated the global selection.
            // This lease still owns an explicit pre-output quota rejection and must be
            // allowed to join a freshly verified candidate rather than expose the old error.
            if(!RotationEnabled || lease.Accepted || !ready || requireNativeBinding && !HasCurrentNativeBinding()||
                quotaCooldowns.TryGetValue(candidate,out var coolingUntil)&&coolingUntil>DateTimeOffset.UtcNow)return false;
            state.Select(candidate);
            selected=candidate;
            lease.SetCredential(candidate,credential);
            failure=null;lastUsed=null;lastCompleted=null;lastResponse=null;
            lastAutoRotation=new(from,candidate,"selected",DateTimeOffset.UtcNow);
            return true;
        }
        finally {gate.Release();}
    }
    private async Task FinishAsync(string profileId, bool accepted, bool completed, bool inference, RoutingResponseEvidence? evidence)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (inference)
            {
                lastUsed = accepted ? profileId : null;
                lastCompleted = completed ? profileId : null;
                lastResponse = evidence;
                if(lastAutoRotation?.ToAccountId==profileId)lastAutoRotation=lastAutoRotation with {State=completed?"completed":accepted?"accepted":"failed"};
            }
            active--;
        }
        finally { gate.Release(); }
    }
    public async Task<object> EvidenceAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return new { activeRequests = active, selectedProfile = selected, lastUsedProfile = lastUsed, lastCompletedProfile = lastCompleted }; }
        finally { gate.Release(); }
    }
    private bool HasCurrentNativeBinding()
    {
        if (!nativeObserved) return false;
        if (NativeBindingCurrent is null) return true;
        var current = false;
        try { current = NativeBindingCurrent(); } catch { }
        if (current) return true;
        nativeObserved = false;
        lastUsed = null;
        lastCompleted = null;
        failure = new RoutingFailure("native-session-ended",
            "已核验的后台已退出或重新启动，原连接已失效。请正常退出 Codex，再从「Codex（订阅切换）」打开。", null, DateTimeOffset.UtcNow);
        return false;
    }
    private LiveStatus Status(string message)
    {
        var binding = !requireNativeBinding || HasCurrentNativeBinding();
        return new(true, ready && binding, active != 0, selected,
            !ready ? "订阅转发服务没有运行，未切换。" : !binding ? "转发服务已启动，但尚未收到官方桌面的有效请求；不能宣称已接入或已切换。" : message)
        {
            VerificationState = lastCompleted == selected ? "completed" : lastUsed == selected ? "accepted" : "unverified",
            LastCompletedAccountId = lastCompleted,
            NativeConnectionObserved = nativeObserved,
            Failure = failure,
            LastResponse = lastResponse,
            RemovalSupported = RemoveSavedAccount is not null,
            AutoRotationSupported = rotation is not null && RotationCandidate is not null,
            AutoRotationEnabled = RotationEnabled,
            LastAutoRotation = lastAutoRotation,
            NativeInput=NativeInputStatus?.Invoke()
        };
    }

    public sealed class RequestLease(RoutingController owner, string profileId, LiveCredential credential, bool inference) : IAsyncDisposable
    {
        private int disposed;
        internal string ProfileId {get;private set;} = profileId;
        public LiveCredential Credential { get; private set; } = credential;
        public bool RotationAllowed {get;init;}
        internal void SetCredential(string id,LiveCredential value) {ProfileId=id;Credential=value;}
        public bool Accepted { get; set; }
        public bool Completed { get; set; }
        public RoutingResponseEvidence? Evidence { get; set; }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                await owner.FinishAsync(ProfileId, Accepted, Completed, inference, Evidence).ConfigureAwait(false);
        }
    }
}
