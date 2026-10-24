using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.ModelRouting;

/// <summary>
/// Desktop authorization is checked against the latest managed file on every
/// request. Selecting a model profile never updates or refreshes desktop auth.
/// JWT claims are structural consistency checks, not signature authentication.
/// </summary>
public sealed class RouterCredentials
{
    private const string Unavailable = "模型账号凭据无法可靠确认，请重新通过官方登录保存该账号后再试。";
    private const string DesktopRefreshDenied = "桌面主账号的令牌由原生 Codex 刷新。请在桌面完成正常登录或刷新后重试；工具不会自行刷新或改写桌面认证。";
    private readonly VaultStore vault;
    private readonly string codexHome;
    private readonly ILiveCredentials saved;
    private readonly string desktopProfileId;
    private readonly TimeProvider clock;
    public string DesktopProfileId => desktopProfileId;

    public RouterCredentials(VaultStore vault, string codexHome, ILiveCredentials saved,
        string desktopProfileId, TimeProvider? timeProvider = null)
    {
        if (!LiveState.ValidId(desktopProfileId)) throw new UserFacingException(Unavailable);
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));
        this.codexHome = AuthFileSafety.NormalizeDirectory(codexHome);
        this.saved = saved ?? throw new ArgumentNullException(nameof(saved));
        this.desktopProfileId = desktopProfileId;
        clock = timeProvider ?? TimeProvider.System;
    }

    // bearer is the token value only. The listener must strictly parse the
    // Authorization header and reject multiple schemes/headers before calling.
    public bool Authorize(string? bearer, string? accountId)
    {
        if (!SafeValue(bearer) || !SafeValue(accountId)) return false;
        try
        {
            var current = ReadDesktop();
            return FixedEquals(accountId!, current.AccountId) && FixedEquals(bearer!, current.AccessToken);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { return false; }
    }

    public async Task<LiveCredential> GetAsync(string selectedId, CancellationToken cancellation)
    {
        if (!LiveState.ValidId(selectedId)) throw new UserFacingException(Unavailable);
        cancellation.ThrowIfCancellationRequested();
        if (selectedId == desktopProfileId) return ReadDesktop();
        try { return ValidateSaved(await saved.GetAsync(selectedId, false, cancellation).ConfigureAwait(false), selectedId); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new UserFacingException(Unavailable); }
    }

    public async Task<LiveCredential> RefreshAsync(string selectedId, CancellationToken cancellation)
    {
        if (!LiveState.ValidId(selectedId)) throw new UserFacingException(Unavailable);
        if (selectedId == desktopProfileId) throw new UserFacingException(DesktopRefreshDenied);
        cancellation.ThrowIfCancellationRequested();
        try { return ValidateSaved(await saved.GetAsync(selectedId, true, cancellation).ConfigureAwait(false), selectedId); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new UserFacingException(Unavailable); }
    }

    private LiveCredential ReadDesktop()
    {
        try
        {
            using var directories = AuthFileSafety.ProtectDirectories(codexHome);
            CodexRuntime.EnsureFileAuthStore(codexHome);
            using var image = AuthFileSafety.ReadOptional(Path.Combine(codexHome, "auth.json"), AuthSnapshot.MaximumBytes);
            if (image is null) throw new UserFacingException(Unavailable);
            using var snapshot = AuthSnapshot.FromBytes(image.Bytes);
            if (snapshot.Info.IdentityKey != desktopProfileId) throw new UserFacingException(Unavailable);
            return FromSnapshot(snapshot);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { throw new UserFacingException(Unavailable); }
    }

    private LiveCredential ValidateSaved(LiveCredential credential, string profile)
    {
        if (credential is null || credential.Info.IdentityKey != profile ||
            !SafeValue(credential.AccessToken) || !SafeValue(credential.AccountId))
            throw new UserFacingException(Unavailable);
        using var snapshot = vault.ReadForRefresh(profile, TimeSpan.FromMilliseconds(500));
        if (snapshot.Info.IdentityKey != profile) throw new UserFacingException(Unavailable);
        var current = FromSnapshot(snapshot);
        // Re-reading the encrypted profile prevents returning a stale result when
        // the user has just replaced that profile through a new official login.
        if (!FixedEquals(current.AccessToken, credential.AccessToken) ||
            !FixedEquals(current.AccountId, credential.AccountId))
            throw new UserFacingException(Unavailable);
        return current;
    }

    private LiveCredential FromSnapshot(AuthSnapshot snapshot)
    {
        var bytes = snapshot.CopyBytes();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var tokens = document.RootElement.GetProperty("tokens");
            var access = Required(tokens, "access_token");
            var id = Required(tokens, "id_token");
            var accountId = Required(tokens, "account_id");
            using var identity = new JwtDocument(id);
            using var bearer = new JwtDocument(access);
            var identityClaims = identity.Root;
            var accessClaims = bearer.Root;
            if (!OfficialIssuer(Required(identityClaims, "iss")) ||
                !OfficialIssuer(Required(accessClaims, "iss")) ||
                !Audience(identityClaims, allowApiResource: false) ||
                !Audience(accessClaims, allowApiResource: true))
                throw new UserFacingException(Unavailable);
            var subject = Required(identityClaims, "sub");
            if (subject != Required(accessClaims, "sub")) throw new UserFacingException(Unavailable);
            if (!accessClaims.TryGetProperty("exp", out var expiration) || !expiration.TryGetInt64(out var seconds))
                throw new UserFacingException(Unavailable);
            var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var now = clock.GetUtcNow();
            if (expires <= now || expires > now.AddDays(35)) throw new UserFacingException(Unavailable);
            if (accessClaims.TryGetProperty("iat", out var issued) &&
                (!issued.TryGetInt64(out var created) || DateTimeOffset.FromUnixTimeSeconds(created) > now.AddMinutes(5)))
                throw new UserFacingException(Unavailable);
            foreach (var claims in new[] { identityClaims, accessClaims })
            {
                var claimAccount = Optional(claims, "chatgpt_account_id");
                if (claims.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
                    claimAccount ??= Optional(auth, "chatgpt_account_id");
                if (claimAccount is not null && claimAccount != accountId) throw new UserFacingException(Unavailable);
            }
            if (snapshot.Info.ExpiresAtUtc != expires) throw new UserFacingException(Unavailable);
            return new LiveCredential(accountId, access,
                snapshot.Info.Plan == "未知" ? "unknown" : snapshot.Info.Plan.ToLowerInvariant(), snapshot.Info);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool Audience(JsonElement claims, bool allowApiResource)
    {
        if (!claims.TryGetProperty("aud", out var value)) return false;
        var entries = value.ValueKind switch
        {
            JsonValueKind.String => new[] { value.GetString() },
            JsonValueKind.Array => value.EnumerateArray().Select(element =>
                element.ValueKind == JsonValueKind.String ? element.GetString() : null).ToArray(),
            _ => []
        };
        return entries.Any(entry => entry is not null &&
            (entry.StartsWith("app_", StringComparison.Ordinal) && entry.Length is > 4 and <= 200 &&
             entry.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
             allowApiResource && (entry is "https://api.openai.com" or "https://api.openai.com/v1" or "https://api.openai.com/auth")));
    }

    private static bool OfficialIssuer(string issuer) =>
        Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "auth.openai.com" && uri.Port == 443 && uri.AbsolutePath == "/" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool SafeValue(string? value) => value is { Length: > 0 and <= AuthSnapshot.MaximumBytes } &&
        !value.Any(char.IsControl);
    private static string Required(JsonElement value, string name) => Optional(value, name) ?? throw new UserFacingException(Unavailable);
    private static string? Optional(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return SafeValue(text) ? text : throw new UserFacingException(Unavailable);
    }

    private static bool FixedEquals(string first, string second)
    {
        var a = Encoding.UTF8.GetBytes(first);
        var b = Encoding.UTF8.GetBytes(second);
        try { return CryptographicOperations.FixedTimeEquals(a, b); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    private sealed class JwtDocument : IDisposable
    {
        private readonly byte[] bytes;
        private readonly JsonDocument document;
        public JwtDocument(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) throw new UserFacingException(Unavailable);
            bytes = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
                .PadRight((parts[1].Length + 3) / 4 * 4, '='));
            try
            {
                document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new UserFacingException(Unavailable);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name)) throw new UserFacingException(Unavailable);
            }
            catch
            {
                document?.Dispose();
                CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
        internal JsonElement Root => document.RootElement;
        public void Dispose() { document.Dispose(); CryptographicOperations.ZeroMemory(bytes); }
    }
}
