using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.LiveSession;

/// <summary>RFC 6455 over a CurrentUserOnly pipe; the backend capability is never sent to the desktop.</summary>
public sealed class DesktopGateway
{
    private const int MaximumHeaders = 8192, MaximumConnections = 8;
    private readonly string pipeName;
    private readonly Uri backendUrl;
    private readonly string capability;
    private readonly LiveBackend backend;
    private readonly ConcurrentDictionary<string, ClientState> clients = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int connectedClients, initializedClients, started;

    public DesktopGateway(string pipeName, Uri backendUrl, string cap, LiveBackend backend)
    {
        LiveWire.ValidateEndpoint(backendUrl, cap);
        if (pipeName.Length is < 16 or > 128 || pipeName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            throw new UserFacingException("受管桌面管道名称无效。");
        this.pipeName = pipeName;
        this.backendUrl = backendUrl;
        capability = cap;
        this.backend = backend;
    }

    public int ConnectedClientCount => Volatile.Read(ref connectedClients);
    public int InitializedClientCount => Volatile.Read(ref initializedClients);
    public IReadOnlyList<int> InitializedClientProcessIds => clients.Values.Where(client => client.Initialized).Select(client => client.ProcessId).ToArray();
    public Task Ready => ready.Task;

    public async Task RunAsync(CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("受管桌面入口仅支持 Windows。");
        if (Interlocked.Exchange(ref started, 1) != 0) throw new UserFacingException("受管桌面入口不能重复运行。");
        var connections = new List<Task>();
        using var slots = new SemaphoreSlim(MaximumConnections);
        NamedPipeServerStream? pending = null;
        try
        {
            pending = CreatePipe();
            ready.TrySetResult();
            while (!cancellation.IsCancellationRequested)
            {
                await slots.WaitAsync(cancellation);
                var transferred = false;
                try
                {
                    pending ??= CreatePipe();
                    await pending.WaitForConnectionAsync(cancellation);
                    var accepted = pending;
                    pending = null;
                    connections.Add(HandleAsync(accepted, slots, cancellation));
                    transferred = true;
                    connections.RemoveAll(task => task.IsCompletedSuccessfully);
                }
                finally { if (!transferred) slots.Release(); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            ready.TrySetException(new UserFacingException("受管桌面入口无法建立。"));
            throw new UserFacingException("受管桌面入口未完成，未连接原生私有 IPC。");
        }
        finally
        {
            if (pending is not null) await pending.DisposeAsync();
            foreach (var client in clients.Values) client.Stop.Cancel();
            try { await Task.WhenAll(connections); } catch { }
        }
    }

    private NamedPipeServerStream CreatePipe() => new(pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);

    private async Task HandleAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken shutdown)
    {
        await using var ownedPipe = pipe;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var peer = new ClientWebSocket();
        using var peerSend = new SemaphoreSlim(1, 1);
        using var frontSend = new SemaphoreSlim(1, 1);
        WebSocket? frontend = null;
        ClientState? client = null;
        var upgraded = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) || clientPid == 0 || clientPid > int.MaxValue)
                throw new UserFacingException("无法确认桌面入口的客户端进程。");
            var key = await ReadUpgradeAsync(pipe, handshake.Token);
            if (!backend.IsEndpointOwned()) throw new UserFacingException("后台监听端点归属无法确认，已拒绝桌面连接。");
            peer.Options.SetRequestHeader("Authorization", "Bearer " + capability);
            peer.Options.Proxy = null;
            peer.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await peer.ConnectAsync(backendUrl, handshake.Token);
            await WriteUpgradeAsync(pipe, key, handshake.Token);
            upgraded = true;
            frontend = WebSocket.CreateFromStream(pipe, new WebSocketCreationOptions
            {
                IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20)
                // Extension offers are accepted, but compression/subprotocols are not negotiated.
            });
            client = new ClientState(Guid.NewGuid().ToString("N"), checked((int)clientPid), stop);
            clients[client.Id] = client;
            Interlocked.Increment(ref connectedClients);
            var outgoing = FromDesktopAsync(frontend, peer, frontSend, peerSend, client, stop.Token);
            var incoming = ToDesktopAsync(peer, frontend, peerSend, frontSend, client, stop.Token);
            await Task.WhenAny(outgoing, incoming);
            stop.Cancel();
            frontend.Abort();
            peer.Abort();
            try { await Task.WhenAll(outgoing, incoming); } catch { }
        }
        catch
        {
            if (!upgraded) await RejectAsync(pipe);
        }
        finally
        {
            stop.Cancel();
            frontend?.Abort();
            frontend?.Dispose();
            peer.Abort();
            if (client is not null)
            {
                backend.MarkFrontendDisconnected(client.Id);
                foreach (var lease in client.Pending.Values) lease.Release(confirmed: false);
                client.Pending.Clear();
                clients.TryRemove(client.Id, out _);
                if (client.Initialized) Interlocked.Decrement(ref initializedClients);
                Interlocked.Decrement(ref connectedClients);
            }
            slots.Release();
        }
    }

    private async Task FromDesktopAsync(WebSocket frontend, ClientWebSocket peer,
        SemaphoreSlim frontendSend, SemaphoreSlim peerSend, ClientState client, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var bytes = await LiveWire.ReceiveAsync(frontend, cancellation);
            if (bytes is null) return;
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                var packet = document.RootElement;
                if (packet.ValueKind != JsonValueKind.Object) throw new UserFacingException("桌面 RPC 格式不支持。");
                var method = LiveWire.String(packet, "method");
                var rpcId = LiveWire.RpcId(packet);
                if (method is not null && BlocksAuthenticationChange(method))
                {
                    if (packet.TryGetProperty("id", out var id))
                        await SendPacketAsync(frontend, frontendSend, new { id, error = new { code = -32601, message = "请通过账号面板管理受管账号；原生登录/退出不会更改认证来源。" } }, cancellation);
                    continue;
                }
                if (method == "initialize")
                {
                    if (rpcId is null) throw new UserFacingException("桌面初始化缺少请求标识。");
                    lock (client.Gate)
                    {
                        if (client.InitializeRequest is not null) throw new UserFacingException("桌面初始化请求重复。");
                        client.InitializeRequest = rpcId;
                    }
                }
                if (method == "initialized")
                {
                    lock (client.Gate) client.InitializedNotification = true;
                    CountInitialization(client);
                }
                if (method is not null && LiveBackend.StartsWork(method))
                {
                    if (rpcId is null) throw new UserFacingException("任务启动必须携带可确认的请求标识。");
                    await backend.TrafficGate.WaitAsync(cancellation);
                    WorkLease? lease = null;
                    try
                    {
                        if (!backend.Connected || !backend.IsEndpointOwned())
                            throw new UserFacingException("受管账号身份或后台端点未验证，已拒绝启动新任务。");
                        backend.TrackFrontendRequest(client.Id, rpcId, method);
                        lease = new WorkLease(backend, client.Id, rpcId);
                        if (!client.Pending.TryAdd(rpcId, lease)) throw new UserFacingException("未完成的桌面任务请求标识重复。");
                        await LiveWire.SendAsync(peer, peerSend, bytes, cancellation);
                    }
                    catch
                    {
                        if (lease is null) backend.TrafficGate.Release();
                        else lease.Release(confirmed: false);
                        throw;
                    }
                }
                else await LiveWire.SendAsync(peer, peerSend, bytes, cancellation);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private async Task ToDesktopAsync(ClientWebSocket peer, WebSocket frontend,
        SemaphoreSlim peerSend, SemaphoreSlim frontendSend, ClientState client, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var bytes = await LiveWire.ReceiveAsync(peer, cancellation);
            if (bytes is null) return;
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
                var packet = document.RootElement;
                var method = LiveWire.String(packet, "method");
                if (method == "account/chatgptAuthTokens/refresh" && packet.TryGetProperty("id", out var id))
                {
                    try
                    {
                        var previous = packet.TryGetProperty("params", out var parameters) ? LiveWire.String(parameters, "previousAccountId") : null;
                        var credential = await backend.RefreshAsync(previous, cancellation);
                        await SendPacketAsync(peer, peerSend, new { id, result = new { accessToken = credential.AccessToken, chatgptAccountId = credential.AccountId, chatgptPlanType = credential.Plan } }, cancellation);
                    }
                    catch
                    {
                        await SendPacketAsync(peer, peerSend, new { id, error = new { code = -32000, message = "受管刷新未完成；未交给不支持的桌面认证处理器。" } }, cancellation);
                    }
                    continue;
                }
                if (method is not null) backend.ObserveNotification(packet);
                else if (LiveWire.RpcId(packet) is { } rpcId)
                {
                    if (client.Pending.TryRemove(rpcId, out var lease)) lease.Release(confirmed: true);
                    lock (client.Gate)
                        if (rpcId == client.InitializeRequest && !packet.TryGetProperty("error", out _) && packet.TryGetProperty("result", out _))
                            client.InitializeResponse = true;
                    CountInitialization(client);
                }
                // Approvals, tools, results and notifications retain their original JSON bytes.
                await LiveWire.SendAsync(frontend, frontendSend, bytes, cancellation);
                if (method == "turn/completed") await backend.StatusAsync(cancellation);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private void CountInitialization(ClientState client)
    {
        lock (client.Gate)
            if (!client.Initialized && client.InitializeResponse && client.InitializedNotification)
            {
                client.Initialized = true;
                Interlocked.Increment(ref initializedClients);
            }
    }

    private static bool BlocksAuthenticationChange(string method) =>
        method.StartsWith("account/login/", StringComparison.Ordinal) || method == "account/logout" ||
        method.StartsWith("account/sessions", StringComparison.Ordinal) ||
        method is "loginApiKey" or "loginChatGpt" or "cancelLoginChatGpt" or "logout";

    private static async Task SendPacketAsync(WebSocket socket, SemaphoreSlim gate, object packet, CancellationToken cancellation)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        try { await LiveWire.SendAsync(socket, gate, bytes, cancellation); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<string> ReadUpgradeAsync(Stream stream, CancellationToken cancellation)
    {
        var bytes = new byte[MaximumHeaders];
        try
        {
            var used = 0;
            while (true)
            {
                if (used == bytes.Length || await stream.ReadAsync(bytes.AsMemory(used, 1), cancellation) == 0)
                    throw new UserFacingException("桌面 WebSocket 握手不完整。");
                var value = bytes[used++];
                if (value > 127 || value < 32 && value is not (9 or 10 or 13)) throw new UserFacingException("桌面握手格式不支持。");
                if (used >= 4 && bytes.AsSpan(used - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var lines = Encoding.ASCII.GetString(bytes, 0, used).Split("\r\n", StringSplitOptions.None);
            var request = lines[0].Split(' ');
            if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" ||
                !request[1].StartsWith('/') || request[1].Contains('?') || request[1].Contains('#') || request[1].Contains('\\'))
                throw new UserFacingException("桌面 WebSocket 请求目标不支持。");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(value => value.Length != 0))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || line[0] is ' ' or '\t') throw new UserFacingException("桌面握手头格式不支持。");
                var name = line[..colon];
                if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
                    !headers.TryAdd(name, line[(colon + 1)..].Trim())) throw new UserFacingException("桌面握手头重复或无效。");
            }
            if (headers.ContainsKey("Origin")) throw new UserFacingException("浏览器 Origin 不允许连接本机桌面入口。");
            if (!headers.TryGetValue("Host", out var host) || !Uri.TryCreate("http://" + host, UriKind.Absolute, out var authority) ||
                authority.Host != "localhost" || !string.IsNullOrEmpty(authority.UserInfo) || authority.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment) ||
                !headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
                !headers.TryGetValue("Connection", out var connection) || !connection.Split(',').Any(value => value.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) ||
                !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13" ||
                !headers.TryGetValue("Sec-WebSocket-Key", out var key)) throw new UserFacingException("桌面 WebSocket 升级信息无效。");
            var decoded = Convert.FromBase64String(key);
            try { if (decoded.Length != 16 || Convert.ToBase64String(decoded) != key) throw new UserFacingException("桌面握手随机数无效。"); }
            finally { CryptographicOperations.ZeroMemory(decoded); }
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task WriteUpgradeAsync(Stream stream, string key, CancellationToken cancellation)
    {
        var challenge = Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        var digest = SHA1.HashData(challenge); // Required RFC 6455 handshake, not a secret hash.
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + Convert.ToBase64String(digest) + "\r\n\r\n");
        try { await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); }
        finally { CryptographicOperations.ZeroMemory(challenge); CryptographicOperations.ZeroMemory(digest); CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task RejectAsync(Stream stream)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var bytes = "HTTP/1.1 403 Rejected\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"u8.ToArray();
        try { await stream.WriteAsync(bytes, deadline.Token); await stream.FlushAsync(deadline.Token); }
        catch { }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class ClientState(string id, int processId, CancellationTokenSource stop)
    {
        internal string Id { get; } = id;
        internal int ProcessId { get; } = processId;
        internal CancellationTokenSource Stop { get; } = stop;
        internal object Gate { get; } = new();
        internal string? InitializeRequest { get; set; }
        internal bool InitializeResponse { get; set; }
        internal bool InitializedNotification { get; set; }
        internal bool Initialized { get; set; }
        internal ConcurrentDictionary<string, WorkLease> Pending { get; } = new(StringComparer.Ordinal);
    }
    private sealed class WorkLease(LiveBackend backend, string connection, string rpcId)
    {
        private int released;
        internal void Release(bool confirmed)
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            if (confirmed) backend.CompleteFrontendRequest(connection, rpcId);
            else backend.MarkFrontendDisconnected(connection);
            backend.TrafficGate.Release();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
