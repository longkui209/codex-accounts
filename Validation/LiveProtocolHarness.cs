using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.Validation;

/// <summary>Restricted integration test entry point. It never launches or attaches a desktop.</summary>
public static class LiveProtocolHarness
{
    private static readonly string AllowedParent = ValidationPaths.OutputDirectory;

    public static async Task RunAsync(string isolatedRoot, Stream input, Stream output)
    {
        var root = ValidateRoot(isolatedRoot);
        using var directories = AuthFileSafety.ProtectDirectories(root);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var stop = new CancellationTokenSource();
        using var firstDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var line = await reader.ReadLineAsync(firstDeadline.Token);
        if (line is null || line.Length > 8192) throw Refusal();
        using var configDocument = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 4 });
        var config = configDocument.RootElement;
        if (!Uri.TryCreate(String(config, "backendUrl"), UriKind.Absolute, out var url) ||
            url.Scheme != "ws" || url.Host != "127.0.0.1" || url.Port < 1024 ||
            url.AbsolutePath != "/" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw Refusal();
        var capability = String(config, "capability");
        if (capability.Length != 43 || capability.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw Refusal();
        var profileA = String(config, "profileA");
        var profileB = String(config, "profileB");
        var pipeName = String(config, "pipeName");
        const string prefix = "codex-account-live-test-";
        if (!pipeName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(pipeName[prefix.Length..], "N", out _) ||
            !LiveState.ValidId(profileA) || !LiveState.ValidId(profileB) || profileA == profileB)
            throw Refusal();
        using var child = Process.GetProcessById(config.GetProperty("backendPid").GetInt32());
        if (child.HasExited || !child.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
            child.StartTime.ToUniversalTime() < Directory.GetCreationTimeUtc(root).AddSeconds(-2))
            throw Refusal();
        var vault = new VaultStore(Path.Combine(root, "vault"));
        foreach (var profile in vault.List()) ValidateSynthetic(vault, profile.Id);
        ValidateSynthetic(vault, profileA);
        ValidateSynthetic(vault, profileB);
        using var deniedNetwork = new DeniedNetwork();
        using var http = new HttpClient(deniedNetwork);
        using var credentials = new LiveCredentials(vault, http);
        await using var backend = new LiveBackend(url, capability, credentials, profileA);
        var ownershipAllowed = true;
        backend.EndpointOwnershipCheck = () => Volatile.Read(ref ownershipAllowed) && OwnedListener.IsOwned(child, url.Port);
        if (!backend.IsEndpointOwned()) throw Refusal();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await backend.ConnectAsync(startup.Token);
        var gateway = new DesktopGateway(pipeName, url, capability, backend);
        var gatewayTask = gateway.RunAsync(stop.Token);
        await gateway.Ready.WaitAsync(TimeSpan.FromSeconds(10));
        var control = new LiveControlServer(pipeName + "-control", backend.StatusAsync, backend.SwitchAsync);
        var controlTask = control.RunAsync(stop.Token);
        var commands = Channel.CreateBounded<JsonElement>(32);
        var busy = false;
        async Task Pump()
        {
            try
            {
                while (await reader.ReadLineAsync(stop.Token) is { } command)
                {
                    if (command.Length > 4096) throw Refusal();
                    using var document = JsonDocument.Parse(command, new JsonDocumentOptions { MaxDepth = 3 });
                    await commands.Writer.WriteAsync(document.RootElement.Clone(), stop.Token);
                }
            }
            finally { commands.Writer.TryComplete(); stop.Cancel(); }
        }
        async Task ProcessCommands()
        {
            await foreach (var command in commands.Reader.ReadAllAsync(stop.Token))
            {
                var ok = true;
                var category = "completed";
                LiveStatus? status = null;
                try
                {
                    switch (String(command, "operation"))
                    {
                        case "status": status = await backend.StatusAsync(stop.Token); break;
                        case "switch":
                            var profile = String(command, "profile");
                            if (profile != profileA && profile != profileB) throw Refusal();
                            ValidateSynthetic(vault, profile);
                            status = await backend.SwitchAsync(profile, stop.Token);
                            break;
                        case "mock-busy":
                            var enabled = command.GetProperty("enabled").GetBoolean();
                            if (enabled && !busy) backend.TrackFrontendRequest("synthetic-harness", "synthetic-busy", "turn/start");
                            if (!enabled && busy) backend.CompleteFrontendRequest("synthetic-harness", "synthetic-busy");
                            busy = enabled;
                            status = await backend.StatusAsync(stop.Token);
                            break;
                        case "endpoint":
                            Volatile.Write(ref ownershipAllowed, command.GetProperty("enabled").GetBoolean());
                            status = await backend.StatusAsync(stop.Token);
                            break;
                        default: throw Refusal();
                    }
                }
                catch { ok = false; category = "operation_rejected"; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = command.TryGetProperty("id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                    ok, category, connected = status?.Connected ?? false, busy = status?.Busy ?? true,
                    selected = backend.CurrentProfileId == profileA ? "A" : backend.CurrentProfileId == profileB ? "B" : "unknown"
                }));
            }
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            ready = true, syntheticOnly = true, controlPipe = pipeName + "-control",
            initialized = gateway.InitializedClientCount > 0
        }));
        var pumpTask = Pump();
        var processingTask = ProcessCommands();
        try { await Task.WhenAny(pumpTask, gatewayTask, controlTask); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(gatewayTask, controlTask, processingTask, pumpTask); } catch { }
        }
        if (deniedNetwork.Calls != 0) throw new UserFacingException("合成集成验证拒绝了意外 OAuth 网络请求。");
    }

    private static string ValidateRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(full), AllowedParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("live-", StringComparison.Ordinal) || !Directory.Exists(full))
            throw Refusal();
        return full;
    }

    private static void ValidateSynthetic(VaultStore vault, string id)
    {
        using var snapshot = vault.Read(id);
        var bytes = snapshot.CopyBytes();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var tokens = document.RootElement.GetProperty("tokens");
            if (!String(tokens, "refresh_token").StartsWith("synthetic-", StringComparison.Ordinal) ||
                !String(tokens, "account_id").StartsWith("synthetic-", StringComparison.Ordinal))
                throw Refusal();
            foreach (var name in new[] { "access_token", "id_token" })
            {
                var token = String(tokens, name);
                var parts = token.Split('.');
                if (parts.Length != 3 || parts[2] != "syntheticSignature")
                    throw Refusal();
                var decoded = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
                    .PadRight((parts[1].Length + 3) / 4 * 4, '='));
                try
                {
                    using var claims = JsonDocument.Parse(decoded);
                    if (!String(claims.RootElement, "sub").StartsWith("synthetic-", StringComparison.Ordinal) ||
                        claims.RootElement.GetProperty("exp").GetInt64() <= DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds())
                        throw Refusal();
                }
                finally { CryptographicOperations.ZeroMemory(decoded); }
            }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String &&
        child.GetString() is { Length: > 0 } result ? result : throw Refusal();

    private static UserFacingException Refusal() => new("仅允许指定验证目录、现场合成凭据和新建官方后台的集成验证。");
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
}
