using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Preserves the desktop account for Realtime; frames are never interpreted as model requests.</summary>
public static class RealtimeRelay
{
    private const string Prefix = "/backend-api/codex";
    private const int MaximumMessageBytes = 16 * 1024 * 1024;
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "authorization", "chatgpt-account-id", "cookie", "set-cookie", "origin",
        "connection", "upgrade", "keep-alive", "transfer-encoding", "proxy-authorization",
        "proxy-authenticate", "te", "trailer", "content-length"
    };
    private static readonly HashSet<string> DesktopHttpRoutes = new(StringComparer.Ordinal)
    {
        "/realtime/calls",
        "/alpha/history/v2/list_windows", "/alpha/history/v2/list_items",
        "/alpha/history/v2/read_item", "/alpha/history/v2/search_contents",
        "/alpha/notes/v2/list_files_by_prefix", "/alpha/notes/v2/read_file",
        "/alpha/notes/v2/search_contents", "/alpha/notes/v2/append_to_file",
        "/alpha/notes/v2/write_file", "/alpha/notes/v2/thread_hint"
    };

    public static bool IsDesktopHttpRoute(string? route) => route is not null && DesktopHttpRoutes.Contains(route);

    // V1/V2 append /realtime. Frameless keeps a backend base unchanged, or replaces /realtime with /live.
    // Frameless sideband appends a call ID; the official HTTP client accepts rtc_ IDs and UUIDs.
    public static bool IsRoute(string? route) => route is "" or "/" or "/realtime" or "/realtime/" or "/live" or "/live/"
        || route is not null && (route.StartsWith("/live/", StringComparison.Ordinal)
            ? ValidCallId(route[6..])
            : route.StartsWith("/", StringComparison.Ordinal) && ValidCallId(route[1..]));

    public static async Task ForwardAsync(HttpContext context, RoutingController controller,
        Uri upstreamBase, CancellationToken cancellation)
    {
        var path = context.Request.Path.Value;
        var route = path?.StartsWith(Prefix, StringComparison.Ordinal) == true ? path[Prefix.Length..] : null;
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Method != "GET" || !IsRoute(route)
            || context.Request.QueryString.Value?.Length > 4096)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var destination = Destination(upstreamBase, route!, context.Request.QueryString);
        var nominations = context.Features.Get<IConnectionNominations>()?.Names;
        // Only ModelRelay's protected frontend should call this. The middleware captures raw hop headers.
        if (nominations is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await using var lease = await controller.BeginAsync(desktopAccount: true, cancellation).ConfigureAwait(false);
        using var upstream = new ClientWebSocket();
        upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        // Follow the existing OS proxy for official TLS. Loopback fixtures cannot leave the local host.
        // Reject redirects so desktop credentials never leave the explicitly selected destination.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = !upstreamBase.IsLoopback,
            Proxy = upstreamBase.IsLoopback ? null : HttpClient.DefaultProxy,
            ConnectTimeout = TimeSpan.FromSeconds(20)
        };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        foreach (var header in context.Request.Headers)
        {
            if (PrivateHeaders.Contains(header.Key) || nominations.Contains(header.Key)
                || header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
            upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
        }
        upstream.Options.SetRequestHeader("Authorization", "Bearer " + lease.Credential.AccessToken);
        upstream.Options.SetRequestHeader("ChatGPT-Account-Id", lease.Credential.AccountId);
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
            upstream.Options.AddSubProtocol(protocol);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, context.RequestAborted);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(20));
            try { await upstream.ConnectAsync(destination, invoker, connect.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
            {
                if (lifetime.IsCancellationRequested) context.Abort();
                else context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }
        }

        using var frontend = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = upstream.SubProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            DangerousEnableCompression = false
        }).ConfigureAwait(false);
        var outbound = ForwardFramesAsync(frontend, upstream, lifetime.Token);
        var inbound = ForwardFramesAsync(upstream, frontend, lifetime.Token);
        try
        {
            var first = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
            var closedNormally = await first.ConfigureAwait(false);
            if (closedNormally && !lifetime.IsCancellationRequested)
            {
                var other = ReferenceEquals(first, outbound) ? inbound : outbound;
                await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token)).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException)
        { /* The peer sees closure; no transport diagnostics or content leave the host. */ }
        finally
        {
            lifetime.Cancel();
            if (frontend.State != WebSocketState.Closed) frontend.Abort();
            if (upstream.State != WebSocketState.Closed) upstream.Abort();
            await ObserveAsync(outbound).ConfigureAwait(false);
            await ObserveAsync(inbound).ConfigureAwait(false);
        }
        // Realtime belongs to desktop A. It does not prove selection or completion of a B inference request.
    }

    internal static async Task<bool> ForwardFramesAsync(WebSocket source, WebSocket destination, CancellationToken cancellation)
    {
        var buffer = new byte[32768];
        long messageBytes = 0;
        WebSocketMessageType? messageType = null;
        try
        {
            while (true)
            {
                var received = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await destination.CloseOutputAsync(received.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            received.CloseStatusDescription, cancellation).ConfigureAwait(false);
                    return true;
                }
                if (received.MessageType is not (WebSocketMessageType.Binary or WebSocketMessageType.Text)
                    || messageType is not null && messageType != received.MessageType)
                    throw new IOException("Realtime frame type was rejected.");
                messageType ??= received.MessageType;
                messageBytes += received.Count;
                if (messageBytes > MaximumMessageBytes)
                {
                    if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await destination.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig,
                            "Realtime message limit exceeded.", cancellation).ConfigureAwait(false);
                    throw new IOException("Realtime message limit exceeded.");
                }
                await destination.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count),
                    received.MessageType, received.EndOfMessage, cancellation).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, received.Count));
                if (received.EndOfMessage) { messageBytes = 0; messageType = null; }
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) when (error is WebSocketException or IOException or OperationCanceledException) { }
    }

    private static Uri Destination(Uri upstreamBase, string route, QueryString query)
    {
        var official = upstreamBase.Scheme == "https" && upstreamBase.Host == "chatgpt.com" && upstreamBase.Port == 443;
        var fixture = upstreamBase.Scheme == "http" && upstreamBase.Host == "127.0.0.1" && upstreamBase.Port > 1024;
        if ((!official && !fixture) || upstreamBase.AbsolutePath.TrimEnd('/') != Prefix
            || upstreamBase.UserInfo.Length != 0 || upstreamBase.Query.Length != 0 || upstreamBase.Fragment.Length != 0)
            throw new ArgumentException("Realtime destination was rejected.");
        return new UriBuilder(upstreamBase)
        {
            Scheme = official ? "wss" : "ws",
            Port = official ? -1 : upstreamBase.Port,
            Path = Prefix + route,
            Query = query.Value?.TrimStart('?') ?? ""
        }.Uri;
    }

    private static bool ValidCallId(string value) => value.Length is > 4 and <= 256
        && (value.StartsWith("rtc_", StringComparison.Ordinal)
            ? value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            : Guid.TryParseExact(value, "D", out _));
}
