using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>Request and response bodies are streamed unchanged. This is never an arbitrary HTTP proxy.</summary>
public sealed class ModelRelay : IAsyncDisposable
{
    private const string Prefix = "/backend-api/codex";
    private static readonly Uri OfficialBase = new("https://chatgpt.com/backend-api/codex/");
    private static readonly HashSet<string> Routes = new(StringComparer.Ordinal)
        { "/responses", "/responses/compact", "/responses/lite", "/responses-lite", "/models",
          "/images/generations", "/images/edits", "/memories/trace_summarize", "/alpha/search" };
    private static readonly HashSet<string> PrivateHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "host", "authorization", "chatgpt-account-id", "cookie", "set-cookie", "x-openai-actor-authorization", "connection", "upgrade", "keep-alive", "transfer-encoding", "proxy-authorization", "proxy-authenticate", "te", "trailer", "content-length" };
    private readonly RouterCredentials credentials;
    private readonly RoutingController controller;
    private readonly HttpClient http;
    private readonly Uri upstreamBase;
    private WebApplication? application;
    private readonly ConcurrentDictionary<string,string> stickyOwners=new(StringComparer.Ordinal);
    public Uri? BaseUri { get; private set; }
    internal Action<IReadOnlySet<string>>? RequestHopEvidenceForTesting { get; set; }
    internal Func<int, bool>? FrontendProcessAllowed { get; set; }
    internal Action<int>? NativeConnectionConfirmed { get; set; }

    public ModelRelay(RouterCredentials credentials, RoutingController controller)
        : this(credentials, controller, new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }, OfficialBase) { }

    // Only the isolated test harness may supply an in-memory handler or a fresh loopback mock.
    internal ModelRelay(RouterCredentials credentials, RoutingController controller, HttpMessageHandler handler, Uri upstreamBase)
    {
        if (upstreamBase != OfficialBase && (upstreamBase.Scheme != "http" || upstreamBase.Host != "127.0.0.1" ||
            upstreamBase.Port <= 1024 || upstreamBase.AbsolutePath != Prefix + "/" || !string.IsNullOrEmpty(upstreamBase.Query) ||
            !string.IsNullOrEmpty(upstreamBase.UserInfo) || !string.IsNullOrEmpty(upstreamBase.Fragment)))
            throw new ArgumentException("Invalid isolated upstream.");
        this.credentials = credentials; this.controller = controller; this.upstreamBase = upstreamBase;
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task StartAsync(CancellationToken cancellation)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0, ConnectionNominations.Install);
            options.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
            options.Limits.MaxRequestHeadersTotalSize = 32768;
            options.Limits.MaxConcurrentConnections = 32;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        application = builder.Build();
        application.UseWebSockets();
        application.Run(ForwardAsync);
        await application.StartAsync(cancellation).ConfigureAwait(false);
        var address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var actual) || actual.Host != "127.0.0.1") throw new IOException();
        BaseUri = new Uri(actual, Prefix);
        await controller.SetReadyAsync(true).ConfigureAwait(false);
    }
    private async Task ForwardAsync(HttpContext context)
    {
        // The connection middleware captures one header block. Close ordinary HTTP
        // after its response so a pipelined request cannot reuse those nominations.
        context.Response.Headers.Connection = "close";
        var request = context.Request;
        var peerPid = 0;
        string? denied = null;
        if (context.Connection.RemoteIpAddress is null || !context.Connection.RemoteIpAddress.Equals(IPAddress.Loopback))
            denied = "local-source";
        else if (request.Host.Host != "127.0.0.1" || request.Host.Port != BaseUri?.Port) denied = "local-host";
        else if (request.Headers.ContainsKey("Origin")) denied = "browser-origin";
        else if (!LoopbackPeer.TryGetCurrentUserProcessId(context.Connection.RemotePort, context.Connection.LocalPort, out peerPid))
            denied = "local-peer";
        else if (FrontendProcessAllowed is not null && !FrontendProcessAllowed(peerPid)) denied = "native-caller";
        if (denied is not null)
        {
            await controller.RecordFailureAsync(denied, 403).ConfigureAwait(false);
            context.Response.StatusCode = 403;
            return;
        }
        var authorization = request.Headers.Authorization;
        var account = request.Headers["ChatGPT-Account-Id"];
        if (authorization.Count != 1 || account.Count != 1 || !authorization[0]!.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !credentials.Authorize(authorization[0]![7..], account[0]))
        {
            await controller.RecordFailureAsync("desktop-auth", 401).ConfigureAwait(false);
            context.Response.StatusCode = 401;
            return;
        }
        var path = request.Path.Value;
        var route = path?.StartsWith(Prefix, StringComparison.Ordinal) == true ? path[Prefix.Length..] : null;
        var voiceUpgrade = request.Headers.Upgrade.Count != 0 && RealtimeRelay.IsRoute(route);
        var desktopHttp = RealtimeRelay.IsDesktopHttpRoute(route);
        if (route is null || !(Routes.Contains(route) || desktopHttp || voiceUpgrade) || request.QueryString.Value?.Length > 4096)
        { context.Response.StatusCode = 404; return; }
        if (request.Headers.Upgrade.Count != 0)
        {
            if (voiceUpgrade)
            {
                await ObserveFrontendAsync(peerPid).ConfigureAwait(false);
                context.Response.Headers.Remove("Connection");
                try { await RealtimeRelay.ForwardAsync(context, controller, upstreamBase, context.RequestAborted).ConfigureAwait(false); }
                catch { if (context.Response.HasStarted) context.Abort(); else { context.Response.Headers.Connection = "close"; context.Response.StatusCode = 502; } }
                return;
            }
            // The official provider handles 426 by resetting WS state and using the full HTTP Responses request.
            if (route == "/responses") await ObserveFrontendAsync(peerPid).ConfigureAwait(false);
            context.Response.StatusCode = route == "/responses" ? 426 : 404; return;
        }
        if (request.Method != (route == "/models" ? "GET" : "POST")) { context.Response.StatusCode = 405; return; }
        // Binding proves a signed, same-user native caller with valid desktop credentials.
        // Upstream acceptance is a separate signal; a rejected saved account must not lock
        // the user out of selecting a different account.
        await ObserveFrontendAsync(peerPid).ConfigureAwait(false);
        try
        {
            var completionRelevant = route is "/responses" or "/responses/lite" or "/responses-lite";
            await using var lease = await controller.BeginAsync(route == "/models" || desktopHttp, context.RequestAborted, completionRelevant).ConfigureAwait(false);
            using var payload=lease.RotationAllowed ? await TransientRequestBuffer.ReadAsync(request.Body,
                request.Headers.ContentEncoding.Any(value=>value!="identity"),context.RequestAborted).ConfigureAwait(false) : null;
            var requestConnectionHeaders = context.Features.Get<IConnectionNominations>()?.Names ??
                throw new IOException("Request header boundary unavailable.");
            RequestHopEvidenceForTesting?.Invoke(requestConnectionHeaders);
            HttpRequestMessage BuildRequest()
            {
                var outgoing=new HttpRequestMessage(new HttpMethod(request.Method),new Uri(upstreamBase,route[1..]+request.QueryString));
                if(request.Method=="POST")outgoing.Content=payload?.Content() ?? new StreamContent(request.Body);
                foreach(var header in request.Headers)
                {
                var sameDesktopActor = (route == "/models" || desktopHttp) && header.Key.Equals("x-openai-actor-authorization", StringComparison.OrdinalIgnoreCase);
                if (PrivateHeaders.Contains(header.Key) && !sameDesktopActor || requestConnectionHeaders.Contains(header.Key) || header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
                if(completionRelevant && header.Key.Equals("x-codex-turn-state",StringComparison.OrdinalIgnoreCase))
                {
                    var value=header.Value.Count==1 ? header.Value[0] : null;
                    if(value is null || !stickyOwners.TryGetValue(StickyKey(value),out var owner) ? lease.ProfileId!=credentials.DesktopProfileId : owner!=lease.ProfileId)continue;
                }
                if(completionRelevant && lease.ProfileId!=credentials.DesktopProfileId && header.Key.Equals("x-codex-routing-hint",StringComparison.OrdinalIgnoreCase))continue;
                if (!outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    outgoing.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
                outgoing.Headers.Authorization=new AuthenticationHeaderValue("Bearer",lease.Credential.AccessToken);
                outgoing.Headers.TryAddWithoutValidation("ChatGPT-Account-Id",lease.Credential.AccountId);
                outgoing.Headers.AcceptEncoding.Clear();outgoing.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                return outgoing;
            }
            var visited=new HashSet<string>(StringComparer.Ordinal){lease.ProfileId};
            using var rotationDeadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var rotationStarted=false;
            var unrecoveredQuota=false;
            HttpResponseMessage? received=null;
            try
            {
                while(true)
                {
                    using var outgoing=BuildRequest();
                    using var headerDeadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    headerDeadline.CancelAfter(TimeSpan.FromSeconds(45));
                    received=await http.SendAsync(outgoing,HttpCompletionOption.ResponseHeadersRead,headerDeadline.Token).ConfigureAwait(false);
                    if(payload is null || !payload.SelfContained || visited.Count>=64)break;
                    var quota=await QuotaRejectionProbe.InspectAsync(received,context.RequestAborted).ConfigureAwait(false);
                    if(!quota.Exhausted)break;
                    if(!rotationStarted){rotationDeadline.CancelAfter(TimeSpan.FromSeconds(30));rotationStarted=true;}
                    var changed=false;
                    try {changed=await controller.TryRotateAsync(lease,payload.Model,visited,quota.ResetsAtUtc,rotationDeadline.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException) when(!context.RequestAborted.IsCancellationRequested){}
                    if(!changed){unrecoveredQuota=true;break;}
                    visited.Add(lease.ProfileId);received.Dispose();received=null;
                }
            }
            catch{received?.Dispose();throw;}
            using var response=received ?? throw new IOException();
            if(completionRelevant && response.Headers.TryGetValues("x-codex-turn-state",out var states))
                foreach(var value in states.Take(1))if(value.Length<=8192){if(stickyOwners.Count>1024)stickyOwners.Clear();stickyOwners[StickyKey(value)]=lease.ProfileId;}
            if (response.StatusCode == HttpStatusCode.Unauthorized && lease.Credential.Info.IdentityKey != credentials.DesktopProfileId)
            {
                await controller.RecordFailureAsync("selected-rejected", 401).ConfigureAwait(false);
                // Do not make native Codex refresh desktop account A for a rejection of selected B.
                // Refresh only B for a subsequent explicit retry; never replay this model request.
                try { _ = await credentials.RefreshAsync(lease.Credential.Info.IdentityKey, context.RequestAborted).ConfigureAwait(false); } catch { }
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("The selected subscription was rejected. Retry or sign in to that account again; desktop sign-in was preserved.", context.RequestAborted);
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            if(unrecoveredQuota)await controller.RecordFailureAsync("quota-exhausted",(int)response.StatusCode).ConfigureAwait(false);
            else if (!response.IsSuccessStatusCode)
                await controller.RecordFailureAsync("upstream-rejected", (int)response.StatusCode).ConfigureAwait(false);
            var responseConnectionHeaders = ConnectionHeaders(response.Headers.Connection);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!PrivateHeaders.Contains(header.Key) && !responseConnectionHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            if (route != "/models" && !desktopHttp) lease.Accepted = response.IsSuccessStatusCode;
            await using var terminal = new ResponseCompletionObserver(response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentEncoding);
            await using var content = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            var buffer = new byte[16384];
            using var idleDeadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            try
            {
                while (true)
                {
                    idleDeadline.CancelAfter(TimeSpan.FromMinutes(5));
                    var read = await content.ReadAsync(buffer, idleDeadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                    await terminal.ObserveDeliveredAsync(buffer.AsMemory(0,read)).ConfigureAwait(false);
                }
            }
            finally
            {
                await terminal.FinishAsync().ConfigureAwait(false);
                lease.Completed = lease.Accepted && terminal.Completed;
                lease.Evidence = new(terminal.Format, terminal.Encoding, (int)response.StatusCode, lease.Completed, DateTimeOffset.UtcNow);
                Array.Clear(buffer);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch
        {
            await controller.RecordFailureAsync("transport-failed", 502).ConfigureAwait(false);
            if (context.Response.HasStarted) context.Abort();
            else { context.Response.StatusCode = 502; await context.Response.WriteAsync("Subscription routing failed; no accepted response was replayed."); }
        }
    }
    private async Task ObserveFrontendAsync(int peerPid)
    {
        if (FrontendProcessAllowed is null) return;
        try { NativeConnectionConfirmed?.Invoke(peerPid); } catch { return; }
        await controller.ObserveNativeConnectionAsync().ConfigureAwait(false);
    }
    private static HashSet<string> ConnectionHeaders(IEnumerable<string?> values) => new(values.Where(value => value is not null)
        .SelectMany(value => value!.Split(',')).Select(value => value.Trim()).Where(value => value.Length != 0), StringComparer.OrdinalIgnoreCase);
    private static string StickyKey(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async ValueTask DisposeAsync()
    {
        await controller.SetReadyAsync(false).ConfigureAwait(false);
        if (application is not null) { await application.StopAsync().ConfigureAwait(false); await application.DisposeAsync().ConfigureAwait(false); }
        http.Dispose();
    }
}
