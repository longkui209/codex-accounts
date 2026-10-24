using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

/// <summary>Owns the control connection to our authenticated App Server, never a desktop-private IPC.</summary>
public sealed class LiveBackend : ILiveSwitchController, IAsyncDisposable
{
    private readonly Uri backendUrl;
    private readonly ILiveCredentials credentials;
    private readonly string initialProfileId;
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly SemaphoreSlim credentialGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> replies = new();
    private readonly ConcurrentDictionary<long, Task> refreshTasks = new();
    private readonly Dictionary<string, string> pendingTraffic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> validatedAccountIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeThreads = new(StringComparer.Ordinal);
    private Task? reader;
    private LoginObservation? loginObservation;
    private string? currentProfileId, effectiveProfileId;
    private bool connected, switching, uncertainTraffic;
    private string message = "受管后台尚未连接。";
    private long nextRequest, nextRefresh;
    private int connectStarted, disposed;

    public LiveBackend(Uri backendUrl, string capability, ILiveCredentials credentials, string initialProfileId)
    {
        LiveWire.ValidateEndpoint(backendUrl, capability);
        ValidateProfile(initialProfileId);
        this.backendUrl = backendUrl;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.initialProfileId = initialProfileId;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + capability);
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public SemaphoreSlim TrafficGate { get; } = new(1, 1);
    public Func<bool>? EndpointOwnershipCheck { get; set; }
    public bool IsEndpointOwned()
    {
        try { return EndpointOwnershipCheck?.Invoke() == true; }
        catch { return false; }
    }
    public string? CurrentProfileId { get { lock (stateGate) return currentProfileId; } }
    public bool Connected { get { lock (stateGate) return connected && socket.State == WebSocketState.Open && IsEndpointOwned(); } }

    public async Task ConnectAsync(CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref connectStarted, 1) != 0)
            throw new UserFacingException("此受管后台控制连接已经使用，不能重复初始化。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await TrafficGate.WaitAsync(deadline.Token);
        try
        {
            RequireEndpointOwned();
            await socket.ConnectAsync(backendUrl, deadline.Token);
            reader = ReadLoopAsync(shutdown.Token);
            await CallAsync("initialize", new
            {
                clientInfo = new { name = "codex_accounts_live_controller", title = "Codex account controller", version = "0.4.0" },
                capabilities = new { experimentalApi = true }
            }, deadline.Token);
            await SendAsync(new { method = "initialized", @params = new { } }, deadline.Token);
            var credential = await GetCredentialAsync(initialProfileId, false, deadline.Token);
            lock (stateGate) { switching = true; effectiveProfileId = initialProfileId; }
            await InstallAndVerifyAsync(credential, deadline.Token);
            lock (stateGate)
            {
                currentProfileId = initialProfileId;
                connected = true;
                switching = false;
                message = "受管后台账号已验证；等待原生桌面连接。";
            }
        }
        catch
        {
            MarkTransportUnavailable();
            socket.Abort();
            throw new UserFacingException("受管后台初始化或账号验证未完成，未报告账号直切成功。");
        }
        finally { TrafficGate.Release(); }
    }

    public async Task<LiveStatus> StatusAsync(CancellationToken cancellation)
    {
        if (!await TrafficGate.WaitAsync(0, cancellation))
            return Snapshot(true, "正在协调任务启动或账号切换，请稍后查询。");
        try { return await QueryStatusUnderGateAsync(cancellation); }
        finally { TrafficGate.Release(); }
    }

    public async Task<LiveStatus> SwitchAsync(string profileId, CancellationToken cancellation)
    {
        ValidateProfile(profileId);
        if (!await TrafficGate.WaitAsync(0, cancellation))
            throw new UserFacingException("有正在协调的任务启动或切换，账号未改变。");
        string? oldProfile = null;
        LiveCredential? oldCredential = null;
        var mutationIssued = false;
        try
        {
            var status = await QueryStatusUnderGateAsync(cancellation);
            if (!status.Connected || status.Busy)
                throw new UserFacingException("后台忙碌或状态无法可靠确认，已拒绝账号直切。");
            lock (stateGate) oldProfile = currentProfileId;
            if (oldProfile == profileId) return Snapshot(false, "受管后台已是所选账号。");
            if (oldProfile is null) throw new UserFacingException("当前受管账号未验证，已拒绝直切。");
            oldCredential = await GetCredentialAsync(oldProfile, false, cancellation);
            var target = await GetCredentialAsync(profileId, false, cancellation);
            lock (stateGate)
            {
                switching = true;
                effectiveProfileId = profileId;
                message = "正在验证受管后台的目标账号。";
            }
            mutationIssued = true;
            await InstallAndVerifyAsync(target, cancellation);
            lock (stateGate)
            {
                currentProfileId = profileId;
                connected = true;
                switching = false;
                message = "受管后台已确认目标工作区；原生桌面仍需保持连接。";
            }
            return Snapshot(false);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException)
        {
            if (!mutationIssued)
            {
                if (failure is UserFacingException) throw;
                throw new UserFacingException("目标凭据或后台状态无法确认，账号未改变。");
            }
            var restored = false;
            if (oldProfile is not null && oldCredential is not null && socket.State == WebSocketState.Open)
            {
                using var rollback = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                rollback.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    lock (stateGate) effectiveProfileId = oldProfile;
                    var latest = await GetCredentialAsync(oldProfile, false, rollback.Token);
                    if (latest.AccountId != oldCredential.AccountId) throw new UserFacingException("原账号凭据身份已经变化，不能自动恢复。");
                    await InstallAndVerifyAsync(latest, rollback.Token);
                    lock (stateGate)
                    {
                        currentProfileId = oldProfile;
                        switching = false;
                        connected = true;
                        message = "目标账号未验证，已重新确认原受管账号。";
                    }
                    restored = true;
                }
                catch { MarkTransportUnavailable(); }
            }
            if (!restored) MarkTransportUnavailable();
            throw new UserFacingException(restored
                ? "直切验证未通过，已恢复并验证原受管账号。"
                : "直切或恢复未完成，受管身份状态不确定；已拒绝继续直切。");
        }
        finally
        {
            lock (stateGate) switching = false;
            TrafficGate.Release();
        }
    }

    public async Task<LiveCredential> RefreshAsync(string? previousAccountId, CancellationToken cancellation)
    {
        await credentialGate.WaitAsync(cancellation);
        try
        {
            RequireEndpointOwned();
            string profile, stableAccount;
            lock (stateGate)
            {
                profile = effectiveProfileId ?? throw new UserFacingException("刷新请求没有已绑定的账号来源。");
                if (!validatedAccountIds.TryGetValue(profile, out stableAccount!))
                    throw new UserFacingException("刷新账号的工作区尚未验证。");
                if (switching && previousAccountId is null)
                    throw new UserFacingException("切换期间的刷新缺少原工作区身份，已拒绝刷新。");
            }
            if (previousAccountId is not null && !string.Equals(previousAccountId, stableAccount, StringComparison.Ordinal))
                throw new UserFacingException("刷新请求的工作区与已绑定账号不一致。");
            // Get(false) may itself refresh near-expiry credentials. Validate against the already
            // bound identity and make exactly one force-refresh call for this backend request.
            var refreshed = await credentials.GetAsync(profile, true, cancellation);
            ValidateCredential(refreshed, profile);
            if (refreshed.AccountId != stableAccount)
                throw new UserFacingException("刷新后的工作区身份发生变化，已拒绝接受。");
            lock (stateGate)
                if (effectiveProfileId != profile) throw new UserFacingException("刷新期间账号归属发生变化，已拒绝接受。");
            return refreshed;
        }
        catch { throw new UserFacingException("受管账号刷新未完成，未输出或替换未知凭据。"); }
        finally { credentialGate.Release(); }
    }

    private async Task<LiveCredential> GetCredentialAsync(string profile, bool refresh, CancellationToken cancellation)
    {
        await credentialGate.WaitAsync(cancellation);
        try
        {
            var credential = await credentials.GetAsync(profile, refresh, cancellation);
            ValidateCredential(credential, profile);
            lock (stateGate)
            {
                if (validatedAccountIds.TryGetValue(profile, out var existing) && existing != credential.AccountId)
                    throw new UserFacingException("同一账号库标识的工作区发生变化，已拒绝接受。");
                validatedAccountIds[profile] = credential.AccountId;
            }
            return credential;
        }
        catch { throw new UserFacingException("账号库凭据读取或身份验证未完成。"); }
        finally { credentialGate.Release(); }
    }

    private static void ValidateProfile(string id)
    {
        if (id.Length != 64 || id.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new UserFacingException("账号库标识无效，已拒绝直切。");
    }

    private static void ValidateCredential(LiveCredential credential, string profile)
    {
        if (credential.Info.IdentityKey != profile || string.IsNullOrWhiteSpace(credential.AccountId) ||
            string.IsNullOrWhiteSpace(credential.AccessToken) || credential.AccessToken.Length > 1024 * 1024 ||
            credential.AccessToken.Any(char.IsControl) || credential.AccountId.Any(char.IsControl))
            throw new UserFacingException("受管账号凭据身份或格式无效。");
    }

    private async Task InstallAndVerifyAsync(LiveCredential credential, CancellationToken cancellation)
    {
        var observation = new LoginObservation();
        lock (stateGate) loginObservation = observation;
        try
        {
            var result = await CallAsync("account/login/start", new
            {
                type = "chatgptAuthTokens", accessToken = credential.AccessToken,
                chatgptAccountId = credential.AccountId, chatgptPlanType = credential.Plan
            }, cancellation);
            if (LiveWire.String(result, "type") != "chatgptAuthTokens")
                throw new UserFacingException("后台没有接受受管的外部认证模式。");
            var succeeded = await observation.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            if (!succeeded) throw new UserFacingException("后台报告认证未完成。");
            var account = await CallAsync("account/read", new { refreshToken = false }, cancellation);
            if (!account.TryGetProperty("account", out var identity) || LiveWire.String(identity, "type") != "chatgpt" ||
                !account.TryGetProperty("workspaceRouting", out var routing) ||
                LiveWire.String(routing, "chatgptAccountId") != credential.AccountId)
                throw new UserFacingException("后台返回的工作区与目标凭据不一致。");
        }
        finally
        {
            lock (stateGate) if (ReferenceEquals(loginObservation, observation)) loginObservation = null;
        }
    }

    private async Task<LiveStatus> QueryStatusUnderGateAsync(CancellationToken cancellation)
    {
        if (!IsEndpointOwned())
        {
            MarkTransportUnavailable();
            return Snapshot(true, "后台端点归属无法确认，已拒绝发送凭据或直切。");
        }
        lock (stateGate)
        {
            if (!connected || socket.State != WebSocketState.Open) return Snapshot(true, "受管后台连接或账号身份尚未验证。");
            if (switching || uncertainTraffic || pendingTraffic.Count != 0)
                return Snapshot(true, "有未完成或状态不确定的任务启动，已拒绝切换。");
        }
        try
        {
            var loaded = await LoadedThreadsAsync(cancellation);
            foreach (var id in loaded)
            {
                var result = await CallAsync("thread/read", new { threadId = id, includeTurns = false }, cancellation);
                if (!result.TryGetProperty("thread", out var thread) || !thread.TryGetProperty("status", out var status))
                    return Snapshot(true, "无法读取已加载任务状态，已拒绝切换。");
                var type = status.ValueKind == JsonValueKind.String ? status.GetString() : LiveWire.String(status, "type");
                if (type != "idle") return Snapshot(true, "已加载任务处于活动或未知状态，已拒绝切换。");
            }
            var after = await LoadedThreadsAsync(cancellation);
            if (!loaded.SetEquals(after)) return Snapshot(true, "任务列表在检查期间发生变化，已拒绝切换。");
            lock (stateGate)
            {
                if (pendingTraffic.Count != 0 || uncertainTraffic || activeThreads.Count != 0)
                    return Snapshot(true, "任务启动或完成状态尚未确认，已拒绝切换。");
            }
            return Snapshot(false, "已加载任务均已确认空闲。");
        }
        catch { return Snapshot(true, "后台任务状态无法可靠查询，已拒绝切换。"); }
    }

    private async Task<HashSet<string>> LoadedThreadsAsync(CancellationToken cancellation)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 64; page++)
        {
            var result = await CallAsync("thread/loaded/list", new { cursor, limit = 100 }, cancellation);
            if (!result.TryGetProperty("data", out var values) || values.ValueKind != JsonValueKind.Array)
                throw new UserFacingException("后台任务列表格式无法确认。");
            foreach (var value in values.EnumerateArray())
            {
                var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (string.IsNullOrEmpty(id) || id.Length > 512 || !ids.Add(id))
                    throw new UserFacingException("后台任务标识无法确认。");
            }
            cursor = LiveWire.String(result, "nextCursor");
            if (cursor is null) return ids;
            if (!cursors.Add(cursor)) break;
        }
        throw new UserFacingException("后台任务列表未能完整读取。");
    }

    public static bool StartsWork(string method) =>
        (method.StartsWith("turn/", StringComparison.Ordinal) && method is not "turn/interrupt") ||
        (method.StartsWith("thread/", StringComparison.Ordinal) &&
         (method.EndsWith("/start", StringComparison.Ordinal) || method.EndsWith("/resume", StringComparison.Ordinal) ||
          method.EndsWith("/fork", StringComparison.Ordinal) || method.EndsWith("/steer", StringComparison.Ordinal)));

    public void TrackFrontendRequest(string connection, string rpcId, string method)
    {
        lock (stateGate)
        {
            if (!pendingTraffic.TryAdd(connection + "\0" + rpcId, method))
            {
                uncertainTraffic = true;
                throw new UserFacingException("桌面重复使用了未完成的请求标识，已拒绝切换。");
            }
        }
    }
    public void CompleteFrontendRequest(string connection, string rpcId)
    {
        lock (stateGate) pendingTraffic.Remove(connection + "\0" + rpcId);
    }
    public void MarkFrontendDisconnected(string connection)
    {
        lock (stateGate)
        {
            var unresolved = pendingTraffic.Keys.Where(key => key.StartsWith(connection + "\0", StringComparison.Ordinal)).ToArray();
            if (unresolved.Length != 0) uncertainTraffic = true;
            foreach (var key in unresolved) pendingTraffic.Remove(key);
        }
    }
    public void ObserveNotification(JsonElement notification)
    {
        var method = LiveWire.String(notification, "method");
        if (!notification.TryGetProperty("params", out var parameters)) return;
        if (method == "account/login/completed")
        {
            // External-token logins have no OAuth login id. Ignore unrelated browser login events.
            if (parameters.TryGetProperty("loginId", out var loginId) && loginId.ValueKind == JsonValueKind.String) return;
            if (parameters.TryGetProperty("success", out var success) && success.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var hasError = parameters.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
                    !(error.ValueKind == JsonValueKind.String && error.GetString() == "");
                lock (stateGate) loginObservation?.Completed.TrySetResult(success.GetBoolean() && !hasError);
            }
            return;
        }
        var threadId = LiveWire.String(parameters, "threadId");
        if (threadId is null) return;
        lock (stateGate)
        {
            if (method == "turn/started") activeThreads.Add(threadId);
            else if (method == "turn/completed") activeThreads.Remove(threadId);
            else if (method == "thread/status/changed" && parameters.TryGetProperty("status", out var status))
            {
                var type = status.ValueKind == JsonValueKind.String ? status.GetString() : LiveWire.String(status, "type");
                if (type == "idle") activeThreads.Remove(threadId); else activeThreads.Add(threadId);
            }
        }
    }

    private LiveStatus Snapshot(bool busy, string? statusMessage = null)
    {
        var owned = IsEndpointOwned();
        lock (stateGate)
            return new LiveStatus(true, connected && socket.State == WebSocketState.Open && owned,
                busy || switching || uncertainTraffic || pendingTraffic.Count != 0 || !owned,
                currentProfileId, statusMessage ?? message);
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellation)
    {
        var id = "live-control-" + Interlocked.Increment(ref nextRequest);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!replies.TryAdd("s:" + id, completion)) throw new UserFacingException("受管 RPC 标识无法确认。");
        try
        {
            await SendAsync(new { id, method, @params = parameters }, cancellation);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellation);
        }
        catch (UserFacingException) { throw; }
        catch { throw new UserFacingException("受管后台 RPC 未完成，未输出原始回复或凭据。"); }
        finally { replies.TryRemove("s:" + id, out _); }
    }

    private async Task SendAsync(object packet, CancellationToken cancellation)
    {
        RequireEndpointOwned();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        try { await LiveWire.SendAsync(socket, sendGate, bytes, cancellation); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task ReadLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var bytes = await LiveWire.ReceiveAsync(socket, cancellation);
                if (bytes is null) break;
                try
                {
                    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                    var packet = document.RootElement;
                    var method = LiveWire.String(packet, "method");
                    if (method == "account/chatgptAuthTokens/refresh" && packet.TryGetProperty("id", out var refreshId))
                    {
                        var sequence = Interlocked.Increment(ref nextRefresh);
                        var cloned = packet.Clone();
                        var task = HandleRefreshAsync(cloned, cancellation);
                        refreshTasks[sequence] = task;
                        _ = task.ContinueWith(completed => refreshTasks.TryRemove(sequence, out _), TaskScheduler.Default);
                    }
                    else if (method is not null) ObserveNotification(packet);
                    else if (LiveWire.RpcId(packet) is { } key && replies.TryRemove(key, out var completion))
                    {
                        if (packet.TryGetProperty("error", out _)) completion.TrySetException(new UserFacingException("受管后台拒绝了本次 RPC。"));
                        else if (packet.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                        else completion.TrySetException(new UserFacingException("受管后台回复格式无法确认。"));
                    }
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { }
        finally
        {
            MarkTransportUnavailable();
            foreach (var pair in replies) pair.Value.TrySetException(new UserFacingException("受管后台控制连接已结束。"));
            replies.Clear();
        }
    }

    private async Task HandleRefreshAsync(JsonElement packet, CancellationToken cancellation)
    {
        var id = packet.GetProperty("id");
        try
        {
            var previous = packet.TryGetProperty("params", out var parameters) ? LiveWire.String(parameters, "previousAccountId") : null;
            var credential = await RefreshAsync(previous, cancellation);
            await SendAsync(new { id, result = new { accessToken = credential.AccessToken, chatgptAccountId = credential.AccountId, chatgptPlanType = credential.Plan } }, cancellation);
        }
        catch
        {
            try { await SendAsync(new { id, error = new { code = -32000, message = "已绑定账号刷新未完成。" } }, cancellation); }
            catch { MarkTransportUnavailable(); }
        }
    }

    private void MarkTransportUnavailable()
    {
        lock (stateGate)
        {
            connected = false;
            uncertainTraffic = true;
            message = "受管后台身份或连接状态不确定，已拒绝继续直切。";
            loginObservation?.Completed.TrySetResult(false);
        }
    }

    private void RequireEndpointOwned()
    {
        if (IsEndpointOwned()) return;
        MarkTransportUnavailable();
        throw new UserFacingException("后台端点不再属于受管官方进程，已拒绝发送能力凭证或账号令牌。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        socket.Abort();
        if (reader is not null) try { await reader; } catch { }
        try { await Task.WhenAll(refreshTasks.Values); } catch { }
        socket.Dispose();
        shutdown.Dispose();
        sendGate.Dispose();
        credentialGate.Dispose();
        TrafficGate.Dispose();
    }

    private sealed class LoginObservation
    {
        internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class LiveWire
{
    internal const int MaximumMessageBytes = 8 * 1024 * 1024;
    internal static void ValidateEndpoint(Uri endpoint, string capability)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("ws" or "wss") || !endpoint.IsLoopback ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            capability.Length < 32 || capability.Length > 256 || capability.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '+' or '/' or '=')))
            throw new UserFacingException("受管后台地址或内存能力凭证无效。");
    }
    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? RpcId(JsonElement packet)
    {
        if (packet.ValueKind != JsonValueKind.Object || !packet.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch { JsonValueKind.String => "s:" + id.GetString(), JsonValueKind.Number => "n:" + id.GetRawText(), _ => null };
    }
    internal static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[65536];
        try
        {
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaximumMessageBytes)
                    throw new UserFacingException("桌面 RPC 消息类型或大小不支持。");
                stream.Write(buffer.AsSpan(0, result.Count));
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);
            return stream.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (stream.TryGetBuffer(out var owned)) CryptographicOperations.ZeroMemory(owned.AsSpan());
        }
    }
    internal static async Task SendAsync(WebSocket socket, SemaphoreSlim gate, ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        if (bytes.Length > MaximumMessageBytes) throw new UserFacingException("桌面 RPC 消息超过允许大小。");
        await gate.WaitAsync(cancellation);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation); }
        finally { gate.Release(); }
    }
}
