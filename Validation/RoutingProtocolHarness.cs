using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Own-process, synthetic-only integration entry point; never starts a desktop.</summary>
public static class RoutingProtocolHarness
{
    private static readonly string AllowedParent = ValidationPaths.OutputDirectory;

    public static async Task RunAsync(string isolatedRoot, Stream input, Stream output)
    {
        var root = ValidateRoot(isolatedRoot);
        using var directories = AuthFileSafety.ProtectDirectories(root);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stop = new CancellationTokenSource();
        using var initialDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = await reader.ReadLineAsync(initialDeadline.Token).ConfigureAwait(false);
        if (first is null || first.Length > 4096) throw Refusal();
        using var configDocument = JsonDocument.Parse(first, new JsonDocumentOptions { MaxDepth = 3 });
        var config = configDocument.RootElement;
        var configNames = config.EnumerateObject().Select(property => property.Name).ToArray();
        if (configNames.Length != 4 || configNames.Distinct(StringComparer.Ordinal).Count() != 4 ||
            configNames.Any(name => name is not ("upstreamURL" or "profileA" or "profileB" or "upstreamPid")))
            throw Refusal();
        if (!Uri.TryCreate(Text(config, "upstreamURL"), UriKind.Absolute, out var upstream) ||
            upstream.Scheme != "http" || upstream.Host != "127.0.0.1" || upstream.Port <= 1024 ||
            upstream.AbsolutePath != "/backend-api/codex/" || upstream.UserInfo.Length != 0 ||
            upstream.Query.Length != 0 || upstream.Fragment.Length != 0) throw Refusal();
        var profileA = Text(config, "profileA");
        var profileB = Text(config, "profileB");
        if (!LiveState.ValidId(profileA) || !LiveState.ValidId(profileB) || profileA == profileB) throw Refusal();
        using var mockOwner = Process.GetProcessById(config.GetProperty("upstreamPid").GetInt32());
        if (mockOwner.HasExited || !OwnedListener.IsOwned(mockOwner, upstream.Port)) throw Refusal();
        var vault = new VaultStore(Path.Combine(root, "vault"));
        var profiles = vault.List();
        if (profiles.Count != 2 || profiles.Any(profile => profile.Id != profileA && profile.Id != profileB)) throw Refusal();
        foreach (var profile in profiles) ValidateSaved(vault, profile.Id);
        var home = Path.Combine(root, "codex-home");
        ValidateDesktop(home, profileA);
        using var deniedNetwork = new DeniedNetwork();
        using var refreshClient = new HttpClient(deniedNetwork);
        using var saved = new LiveCredentials(vault, refreshClient);
        var credentials = new RouterCredentials(vault, home, saved, profileA);
        var state = new LiveState(vault, "routing");
        using var hostLease = state.AcquireHostLease();
        var controller = new RoutingController(credentials, state, profileA, requireNativeBinding: true, new RotationSettingsStore(vault));
        controller.RemoveSavedAccount = id => vault.Remove(id);
        var candidatesAllowed=true;
        controller.RotationCandidate=(_,model,visited,_)=>Task.FromResult(!candidatesAllowed || model is not (null or "synthetic-model") ? null :
            new[]{profileA,profileB}.FirstOrDefault(id=>!visited.Contains(id)));
        var bindingCurrent = true;
        controller.NativeBindingCurrent = () => bindingCurrent;
        var guardedTransport = new SyntheticLoopbackHandler(mockOwner, upstream);
        await using var relay = new ModelRelay(credentials, controller, guardedTransport, upstream);
        // Only this fixture's already-verified mock owner may act as its synthetic frontend.
        relay.FrontendProcessAllowed = pid => pid == mockOwner.Id;
        var incomingEvidenceSeen = 0;
        var incomingNominationObserved = 0;
        relay.RequestHopEvidenceForTesting = names =>
        {
            if (Interlocked.CompareExchange(ref incomingEvidenceSeen, 1, 0) == 0)
                Volatile.Write(ref incomingNominationObserved, names.Contains("X-Synthetic-Hop") ? 1 : 0);
        };
        await relay.StartAsync(stop.Token).ConfigureAwait(false);
        if (relay.BaseUri is null) throw Refusal();
        using var current = Process.GetCurrentProcess();
        var instance = Guid.NewGuid().ToString("N");
        var controlPipe = "codex-accounts-control-" + instance;
        var control = new LiveControlServer(controlPipe, controller.StatusAsync, controller.SwitchAsync, controller.RemoveAsync,controller.ConfigureRotationAsync);
        var controlTask = control.RunAsync(stop.Token);
        state.Publish(new LiveDescriptor(instance, current.Id, current.StartTime.ToUniversalTime().Ticks,
            controlPipe, "codex-accounts-desktop-" + instance, "synthetic-routing-test"));
        var client = new LiveClient(vault, "routing");
        var commands = Channel.CreateBounded<JsonElement>(32);
        var eof = false;
        string Mark(string? id) => id == profileA ? "A" : id == profileB ? "B" : "unknown";

        async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(stop.Token).ConfigureAwait(false);
                    if (line is null) { eof = true; break; }
                    if (line.Length > 4096) throw Refusal();
                    using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 3 });
                    await commands.Writer.WriteAsync(document.RootElement.Clone(), stop.Token).ConfigureAwait(false);
                }
            }
            finally { commands.Writer.TryComplete(); stop.Cancel(); }
        }

        async Task ProcessCommandsAsync()
        {
            await foreach (var command in commands.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                var ok = true;
                var category = "completed";
                try
                {
                    ValidateDesktop(home, profileA);
                    switch (Text(command, "operation"))
                    {
                        case "status":
                        case "evidence":
                            _ = await client.StatusAsync(stop.Token).ConfigureAwait(false);
                            break;
                        case "switch":
                            var selected = Text(command, "profile");
                            if (selected != profileA && selected != profileB) throw Refusal();
                            ValidateSaved(vault, selected);
                            _ = await client.SwitchAsync(selected, stop.Token).ConfigureAwait(false);
                            break;
                        case "binding-current":
                            bindingCurrent = command.GetProperty("current").GetBoolean();
                            break;
                        case "remove":
                            _ = await client.RemoveAsync(Text(command,"profile"),stop.Token).ConfigureAwait(false);
                            break;
                        case "restore-removed":
                            vault.RestoreRemoved(vault.LastRemoved()?.RemovalId ?? throw Refusal());
                            break;
                        case "auto-rotation":
                            _ = await client.ConfigureRotationAsync(command.GetProperty("enabled").GetBoolean(),stop.Token).ConfigureAwait(false);
                            break;
                        case "rotation-candidates":
                            candidatesAllowed=command.GetProperty("allowed").GetBoolean();
                            break;
                        default: throw Refusal();
                    }
                }
                catch { ok = false; category = "operation_rejected"; }
                var status = await controller.StatusAsync(stop.Token).ConfigureAwait(false);
                var evidence = JsonSerializer.SerializeToElement(await controller.EvidenceAsync().ConfigureAwait(false));
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = command.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                    ok, category, connected = status.Connected, busy = status.Busy, selected = Mark(status.CurrentAccountId),
                    nativeConnectionObserved = status.NativeConnectionObserved,
                    failureCode = status.Failure?.Code, failureHttpStatus = status.Failure?.HttpStatus,
                    verificationState = status.VerificationState,
                    responseFormat = status.LastResponse?.Format, responseEncoding = status.LastResponse?.Encoding,
                    savedProfiles = vault.List().Select(entry=>Mark(entry.Id)).ToArray(), removalSupported = status.RemovalSupported,
                    autoRotationEnabled=status.AutoRotationEnabled,autoRotationSupported=status.AutoRotationSupported,
                    lastRotationFrom=Mark(status.LastAutoRotation?.FromAccountId),lastRotationTo=Mark(status.LastAutoRotation?.ToAccountId),lastRotationState=status.LastAutoRotation?.State,
                    activeRequests = evidence.GetProperty("activeRequests").GetInt32(),
                    lastUsed = Mark(evidence.GetProperty("lastUsedProfile").GetString()),
                    lastCompleted = Mark(evidence.GetProperty("lastCompletedProfile").GetString()),
                    blockedOAuthAttempts = deniedNetwork.Calls,
                    incomingNominationObserved = Volatile.Read(ref incomingNominationObserved) == 1
                })).ConfigureAwait(false);
            }
        }

        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                ready = true, syntheticOnly = true, realDesktopConnected = false,
                upstreamOwned = true, relayUrl = relay.BaseUri.AbsoluteUri, controlPipe
            })).ConfigureAwait(false);
            var pumpTask = PumpAsync();
            var processingTask = ProcessCommandsAsync();
            try { await Task.WhenAny(pumpTask, processingTask, controlTask).ConfigureAwait(false); }
            finally
            {
                stop.Cancel();
                try { await Task.WhenAll(controlTask, processingTask, pumpTask).ConfigureAwait(false); } catch { }
            }
            if (!eof) throw Refusal();
        }
        finally
        {
            stop.Cancel();
            try { await controlTask.ConfigureAwait(false); } catch { }
            state.RemoveOwned(instance);
        }
    }

    private static string ValidateRoot(string isolatedRoot)
    {
        var root = Path.GetFullPath(isolatedRoot);
        var name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), AllowedParent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("routing-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[8..], "D", out _) || !Directory.Exists(root)) throw Refusal();
        return root;
    }

    private static void ValidateSaved(VaultStore vault, string profile)
    {
        using var snapshot = vault.Read(profile);
        ValidateSnapshot(snapshot, profile);
    }

    private static void ValidateDesktop(string home, string profile)
    {
        using var directories = AuthFileSafety.ProtectDirectories(home);
        using var image = AuthFileSafety.ReadOptional(Path.Combine(home, "auth.json"), AuthSnapshot.MaximumBytes);
        if (image is null) throw Refusal();
        using var snapshot = AuthSnapshot.FromBytes(image.Bytes);
        ValidateSnapshot(snapshot, profile);
    }

    private static void ValidateSnapshot(AuthSnapshot snapshot, string profile)
    {
        if (snapshot.Info.IdentityKey != profile) throw Refusal();
        var bytes = snapshot.CopyBytes();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var tokens = document.RootElement.GetProperty("tokens");
            if (!Text(tokens, "refresh_token").StartsWith("synthetic-", StringComparison.Ordinal) ||
                !Text(tokens, "account_id").StartsWith("synthetic-", StringComparison.Ordinal)) throw Refusal();
            ValidateToken(Text(tokens, "access_token"));
            ValidateToken(Text(tokens, "id_token"));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void ValidateToken(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 3 || parts[2] != "syntheticSignature") throw Refusal();
        var decoded = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
            .PadRight((parts[1].Length + 3) / 4 * 4, '='));
        try
        {
            using var claims = JsonDocument.Parse(decoded, new JsonDocumentOptions { MaxDepth = 16 });
            var root = claims.RootElement;
            if (!Text(root, "sub").StartsWith("synthetic-", StringComparison.Ordinal) ||
                !Text(root, "email").EndsWith("@example.test", StringComparison.Ordinal) ||
                root.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()) throw Refusal();
        }
        finally { CryptographicOperations.ZeroMemory(decoded); }
    }

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String &&
        child.GetString() is { Length: > 0 and <= 8192 } text ? text : throw Refusal();

    private static UserFacingException Refusal() => new("仅允许指定隔离目录、合成凭据和本次进程持有的本地模拟服务进行订阅路由验证。");

    private sealed class DeniedNetwork : HttpMessageHandler
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            throw new UserFacingException("合成验证禁止访问真实 OAuth。");
        }
    }

    private sealed class SyntheticLoopbackHandler : DelegatingHandler
    {
        private readonly Process owner;
        private readonly Uri upstream;
        internal SyntheticLoopbackHandler(Process owner, Uri upstream)
            : base(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(5) })
        { this.owner = owner; this.upstream = upstream; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri;
            if (url is null || url.Scheme != upstream.Scheme || url.Host != upstream.Host || url.Port != upstream.Port ||
                !url.AbsolutePath.StartsWith(upstream.AbsolutePath, StringComparison.Ordinal) ||
                url.UserInfo.Length != 0 || url.Fragment.Length != 0 || !OwnedListener.IsOwned(owner, upstream.Port) ||
                request.Headers.Authorization?.Scheme != "Bearer" ||
                request.Headers.Authorization.Parameter is not { } bearer ||
                !request.Headers.TryGetValues("ChatGPT-Account-Id", out var ids) ||
                !ids.Single().StartsWith("synthetic-", StringComparison.Ordinal)) throw Refusal();
            // The final transport gate makes an accidental real token fail before any socket write.
            ValidateToken(bearer);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
