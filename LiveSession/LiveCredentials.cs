using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher.LiveSession;

/// <summary>
/// Reads only the DPAPI vault. Cached JWT claims provide account metadata, not
/// independent signature validation. Refresh responses use the fixed official
/// TLS endpoint; the real Codex auth.json is never opened or modified.
/// </summary>
public sealed class LiveCredentials : ILiveCredentials, IDisposable
{
    // Official rust-v0.162.0-alpha.2 login/src/auth/manager.rs defaults.
    // The actual cached id_token audience takes precedence.
    internal const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private static readonly Uri TokenEndpoint = new("https://auth.openai.com/oauth/token");
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HttpDeadline = TimeSpan.FromSeconds(6);
    private const string LoginRequired = "账号凭据未能安全刷新。请重新通过 Codex 官方登录添加该账号后再试；不会修改桌面的 auth.json。";
    private const string ConcurrentLogin = "该账号在刷新期间已重新登录或更新，已保留最新登录；请刷新账号列表后重试，必要时重新通过官方登录添加账号。";
    private readonly VaultStore vault;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly TimeProvider clock;
    private readonly string scopeDigest;
    private int disposed;

    /// <param name="httpClient">For offline tests only: supply a client backed by a synthetic in-memory handler.</param>
    public LiveCredentials(VaultStore vault, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        this.vault = vault;
        clock = timeProvider ?? TimeProvider.System;
        ownsHttp = httpClient is null;
        http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        }) { Timeout = HttpDeadline };
        using var identity = WindowsIdentity.GetCurrent();
        var bytes = Encoding.UTF8.GetBytes((identity.User?.Value ?? throw new UserFacingException(LoginRequired)) +
            "\0" + vault.DirectoryPath.ToUpperInvariant());
        try { scopeDigest = Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task<LiveCredential> GetAsync(string profileId, bool forceRefresh, CancellationToken cancellation)
    {
        if (Volatile.Read(ref disposed) != 0 || profileId is null || profileId.Length != 64 ||
            profileId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return Task.FromException<LiveCredential>(new UserFacingException(LoginRequired));
        // A Windows Mutex is owned by a thread. This entire serialized operation
        // stays on one dedicated thread, including waiting on async HTTP work.
        return Task.Factory.StartNew(() => GetCore(profileId, forceRefresh, cancellation), cancellation,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private LiveCredential GetCore(string profileId, bool forceRefresh, CancellationToken cancellation)
    {
        try
        {
            using var mutex = new Mutex(false, "Global\\CodexAccountSwitcher.LiveRefresh." + scopeDigest + "." + profileId);
            var acquired = false;
            try
            {
                var waiting = Stopwatch.StartNew();
                while (!acquired && waiting.Elapsed < TimeSpan.FromSeconds(2))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try { acquired = mutex.WaitOne(25); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                if (!acquired) throw new UserFacingException("该账号的令牌正在刷新，请稍后重试；若持续失败，请重新通过官方登录添加账号。");
                using var snapshot = vault.ReadForRefresh(profileId, TimeSpan.FromMilliseconds(500));
                if (snapshot.Info.IdentityKey != profileId) throw new UserFacingException(LoginRequired);
                var original = snapshot.CopyBytes();
                try
                {
                    var before = ReadTokens(original);
                    if (!forceRefresh && before.ExpiresAtUtc > clock.GetUtcNow().Add(RefreshWindow))
                        return Credential(before, snapshot.Info);
                    cancellation.ThrowIfCancellationRequested();
                    var rotated = RefreshAsync(before, cancellation).GetAwaiter().GetResult();
                    var updated = Encode(rotated, clock.GetUtcNow());
                    try
                    {
                        using var replacement = AuthSnapshot.FromBytes(updated);
                        var after = ReadTokens(updated);
                        if (replacement.Info.IdentityKey != profileId ||
                            after.AccountId != before.AccountId || after.Subject != before.Subject ||
                            after.ClientId != before.ClientId || after.ExpiresAtUtc <= clock.GetUtcNow())
                            throw new UserFacingException(LoginRequired);
                        // Once the token response has arrived, persist rotation even if
                        // the caller disconnects. Retrying a consumed refresh token is unsafe.
                        if (!vault.CompareAndSave(profileId, snapshot, replacement, TimeSpan.FromMilliseconds(500)))
                            throw new UserFacingException(ConcurrentLogin);
                        return Credential(after, replacement.Info);
                    }
                    finally { CryptographicOperations.ZeroMemory(updated); }
                }
                finally { CryptographicOperations.ZeroMemory(original); }
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (UserFacingException error) when (error.Message == ConcurrentLogin ||
            error.Message == "该账号的令牌正在刷新，请稍后重试；若持续失败，请重新通过官方登录添加账号。")
        { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new UserFacingException(LoginRequired); }
    }

    private async Task<Tokens> RefreshAsync(Tokens before, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(HttpDeadline);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            client_id = before.ClientId, grant_type = "refresh_token", refresh_token = before.RefreshToken
        });
        var responseBytes = new byte[AuthSnapshot.MaximumBytes + 1];
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode ||
                response.RequestMessage?.RequestUri is { } actual && actual != TokenEndpoint ||
                response.Content.Headers.ContentLength > AuthSnapshot.MaximumBytes)
                throw new UserFacingException(LoginRequired);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var used = 0;
            while (used < responseBytes.Length)
            {
                var read = await stream.ReadAsync(responseBytes.AsMemory(used), deadline.Token);
                if (read == 0) break;
                used += read;
            }
            if (used == 0 || used > AuthSnapshot.MaximumBytes) throw new UserFacingException(LoginRequired);
            using var document = JsonDocument.Parse(responseBytes.AsMemory(0, used),
                new JsonDocumentOptions { MaxDepth = 16 });
            EnsureObject(document.RootElement);
            // Alpha2's RefreshResponse returns all three fields as Option<String>.
            // A usable refresh must contain a new access token; unchanged id/refresh
            // tokens remain valid when those optional fields are not returned.
            return new Tokens
            {
                AccountId = before.AccountId,
                AccessToken = Required(document.RootElement, "access_token"),
                IdToken = Optional(document.RootElement, "id_token") ?? before.IdToken,
                RefreshToken = Optional(document.RootElement, "refresh_token") ?? before.RefreshToken,
                OAuthIssuer=before.OAuthIssuer,OAuthClientId=before.OAuthClientId,LoginOwnerId=before.LoginOwnerId
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static LiveCredential Credential(Tokens tokens, AccountInfo info) => new(tokens.AccountId,
        tokens.AccessToken, info.Plan == "未知" ? "unknown" : info.Plan.ToLowerInvariant(), info);

    private static byte[] Encode(Tokens tokens, DateTimeOffset now) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        auth_mode = "chatgpt",
        OPENAI_API_KEY = (string?)null,
        tokens = new { access_token = tokens.AccessToken, id_token = tokens.IdToken,
            refresh_token = tokens.RefreshToken, account_id = tokens.AccountId,
            oauth_provenance=tokens.OAuthIssuer is null?null:new{issuer=tokens.OAuthIssuer,client_id=tokens.OAuthClientId},login_owner_id=tokens.LoginOwnerId },
        last_refresh = now
    });

    private static Tokens ReadTokens(byte[] source)
    {
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 16 });
        var container = document.RootElement.GetProperty("tokens");
        var tokens = new Tokens
        {
            AccountId = Required(container, "account_id"), AccessToken = Required(container, "access_token"),
            IdToken = Required(container, "id_token"), RefreshToken = Required(container, "refresh_token")
        };
        using var identity = Claims(tokens.IdToken);
        using var access = Claims(tokens.AccessToken);
        var id = identity.RootElement;
        var bearer = access.RootElement;
        if (!OfficialIssuer(Required(id, "iss"))) throw new UserFacingException(LoginRequired);
        if (bearer.TryGetProperty("iss", out _) && !OfficialIssuer(Required(bearer, "iss")))
            throw new UserFacingException(LoginRequired);
        tokens.Subject = Required(id, "sub");
        if (Required(bearer, "sub") != tokens.Subject) throw new UserFacingException(LoginRequired);
        tokens.ClientId = ClientId(id);
        tokens.LoginOwnerId=Optional(container,"login_owner_id");
        if(container.TryGetProperty("oauth_provenance",out var provenance)&&provenance.ValueKind!=JsonValueKind.Null)
        {
            tokens.OAuthIssuer=Required(provenance,"issuer");tokens.OAuthClientId=Required(provenance,"client_id");
            if(!OfficialIssuer(tokens.OAuthIssuer)||tokens.OAuthClientId!=tokens.ClientId)throw new UserFacingException(LoginRequired);
        }
        if (!bearer.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds))
            throw new UserFacingException(LoginRequired);
        tokens.ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        foreach (var claims in new[] { id, bearer })
        {
            var account = Optional(claims, "chatgpt_account_id");
            if (claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
                account ??= Optional(auth, "chatgpt_account_id");
            if (account is not null && account != tokens.AccountId) throw new UserFacingException(LoginRequired);
        }
        return tokens;
    }

    private static string ClientId(JsonElement identity)
    {
        if (!identity.TryGetProperty("aud", out var audience)) throw new UserFacingException(LoginRequired);
        var values = audience.ValueKind switch
        {
            JsonValueKind.String => new[] { audience.GetString() },
            JsonValueKind.Array => audience.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray(),
            _ => Array.Empty<string?>()
        };
        var clients = values.Where(value => value is not null && value.StartsWith("app_", StringComparison.Ordinal) &&
                value.Length is > 4 and <= 200 && value.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (clients.Length != 1) throw new UserFacingException(LoginRequired);
        // DefaultClientId is the alpha2 default; another sole app_ audience may
        // belong to the user's actual official desktop login and is kept intact.
        return clients[0]!;
    }

    private static bool OfficialIssuer(string issuer) => Uri.TryCreate(issuer, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "auth.openai.com" && uri.Port == 443 &&
        uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static JsonDocument Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) throw new UserFacingException(LoginRequired);
        var bytes = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
            .PadRight((parts[1].Length + 3) / 4 * 4, '='));
        try
        {
            // ParseValue copies into the document's owned storage. The explicitly
            // owned decoded buffer may then be cleared without invalidating it.
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            var document = JsonDocument.ParseValue(ref reader);
            try { EnsureObject(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string Required(JsonElement container, string name) =>
        Optional(container, name) ?? throw new UserFacingException(LoginRequired);

    private static string? Optional(JsonElement container, string name)
    {
        if (!container.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new UserFacingException(LoginRequired);
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > AuthSnapshot.MaximumBytes / 2 || result.Any(char.IsControl))
            throw new UserFacingException(LoginRequired);
        return result;
    }

    private static void EnsureObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new UserFacingException(LoginRequired);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new UserFacingException(LoginRequired);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsHttp) http.Dispose();
    }

    private sealed class Tokens
    {
        public string AccountId { get; init; } = "";
        public string AccessToken { get; init; } = "";
        public string IdToken { get; init; } = "";
        public string RefreshToken { get; init; } = "";
        public string Subject { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string? OAuthIssuer {get;set;}
        public string? OAuthClientId {get;set;}
        public string? LoginOwnerId {get;set;}
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
