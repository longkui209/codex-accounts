using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountSwitcher.Core;

/// <summary>
/// An owned, disposable copy of a Codex ChatGPT authentication file.
/// JWT claims are read only for local display and deduplication. Parsing them does
/// not authenticate an account or prove that the server will accept its tokens.
/// </summary>
public sealed class AuthSnapshot : IDisposable
{
    public const int MaximumBytes = 1_048_576;
    private const string InvalidMessage =
        "认证文件格式无效或缺少必要的订阅凭据，请通过 Codex 官方登录重新生成。";
    private const string UnsupportedMessage =
        "仅支持通过 Codex 官方登录获得的 ChatGPT 订阅账号；不支持 API Key 或其他认证方式。";
    private readonly object gate = new();
    private byte[]? bytes;

    private AuthSnapshot(byte[] ownedBytes, AccountInfo info,AccountProfile profile)
    {
        bytes = ownedBytes;
        Info = info;
        Profile=profile;
    }

    public AccountInfo Info { get; }
    public AccountProfile Profile { get; }

    public static AuthSnapshot FromBytes(byte[] source)
    {
        if (source is null || source.Length == 0)
            throw new UserFacingException(InvalidMessage);
        if (source.Length > MaximumBytes)
            throw new UserFacingException("认证文件超过 1 MiB，已拒绝读取。");

        var owned = source.ToArray();
        try
        {
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions
            {
                MaxDepth = 16,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            var root = document.RootElement;
            EnsureObject(root);
            EnsureUniqueProperties(root);

            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKey) &&
                apiKey.ValueKind != JsonValueKind.Null)
                throw new UserFacingException(UnsupportedMessage);
            if (!root.TryGetProperty("auth_mode", out var mode) ||
                mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt")
                throw new UserFacingException(UnsupportedMessage);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("auth_mode" or "tokens" or "last_refresh" or "OPENAI_API_KEY"))
                    throw new UserFacingException(UnsupportedMessage);
            }

            if (!root.TryGetProperty("tokens", out var tokens))
                throw new UserFacingException(InvalidMessage);
            EnsureObject(tokens);
            EnsureUniqueProperties(tokens);
            foreach (var property in tokens.EnumerateObject())
            {
                if (property.Name is not ("access_token" or "id_token" or "refresh_token" or "account_id"))
                    throw new UserFacingException(UnsupportedMessage);
            }

            var accountId = RequiredString(tokens, "account_id", 256);
            if (accountId.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                throw new UserFacingException(InvalidMessage);
            var access = ReadJwt(RequiredString(tokens, "access_token", MaximumBytes / 2));
            var identity = ReadJwt(RequiredString(tokens, "id_token", MaximumBytes / 2));
            _ = RequiredString(tokens, "refresh_token", MaximumBytes / 2);
            var subject = identity.Subject ?? access.Subject;
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 512 ||
                subject.Any(char.IsControl))
                throw new UserFacingException(InvalidMessage);
            if (access.Subject is not null && identity.Subject is not null &&
                !string.Equals(access.Subject, identity.Subject, StringComparison.Ordinal))
                throw new UserFacingException(InvalidMessage);

            var identityBytes = Encoding.UTF8.GetBytes(accountId + "\0" + subject);
            string identityKey;
            try { identityKey = Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(identityBytes); }

            var info = new AccountInfo(identityKey,
                MaskEmail(identity.Email ?? access.Email),
                DisplayPlan(access.Plan ?? identity.Plan),
                access.ExpiresAtUtc ?? identity.ExpiresAtUtc);
            var profile=new AccountProfile(identity.DisplayName ?? access.DisplayName,identity.Username ?? access.Username,
                AccountProfile.SafeAvatar(identity.AvatarUrl ?? access.AvatarUrl));
            return new AuthSnapshot(owned, info,profile);
        }
        catch (UserFacingException)
        {
            CryptographicOperations.ZeroMemory(owned);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or
                                         ArgumentException or InvalidOperationException or OverflowException)
        {
            CryptographicOperations.ZeroMemory(owned);
            throw new UserFacingException(InvalidMessage);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(owned);
            throw;
        }
    }

    public static AuthSnapshot Read(string path)
    {
        byte[]? source = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UserFacingException("认证文件不能是符号链接或重解析文件。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes)
                throw new UserFacingException("认证文件超过 1 MiB，已拒绝读取。");
            if (stream.Length == 0)
                throw new UserFacingException(InvalidMessage);
            source = new byte[checked((int)stream.Length)];
            stream.ReadExactly(source);
            return FromBytes(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         System.Security.SecurityException or ArgumentException)
        {
            throw new UserFacingException("无法读取认证文件，请确认文件存在且当前 Windows 用户具有访问权限。");
        }
        finally
        {
            if (source is not null) CryptographicOperations.ZeroMemory(source);
        }
    }

    public byte[] CopyBytes()
    {
        lock (gate)
        {
            if (bytes is null)
                throw new UserFacingException("认证快照已经释放，请重新读取账号。");
            return bytes.ToArray();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (bytes is null) return;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = null;
        }
    }

    public override string ToString() => "ChatGPT 认证快照（凭据已隐藏）";

    private static void EnsureObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new UserFacingException(InvalidMessage);
    }

    private static void EnsureUniqueProperties(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new UserFacingException(InvalidMessage);
    }

    private static string RequiredString(JsonElement container, string name, int maximumLength)
    {
        if (!container.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new UserFacingException(InvalidMessage);
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
            throw new UserFacingException(InvalidMessage);
        return text;
    }

    private static JwtMetadata ReadJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 ||
                part.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))))
            throw new UserFacingException(InvalidMessage);
        var payload = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/')
            .PadRight((parts[1].Length + 3) / 4 * 4, '='));
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            EnsureObject(root);
            EnsureUniqueProperties(root);
            DateTimeOffset? expires = null;
            if (root.TryGetProperty("exp", out var expiration))
            {
                if (!expiration.TryGetInt64(out var seconds))
                    throw new UserFacingException(InvalidMessage);
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            var subject = OptionalString(root, "sub", 512);
            var email = OptionalString(root, "email", 254);
            var plan = OptionalString(root, "chatgpt_plan_type", 64);
            var displayName=DisplayClaim(root,"name",160) ?? DisplayClaim(root,"nickname",160);
            var username=DisplayClaim(root,"preferred_username",160) ?? DisplayClaim(root,"username",160);
            var avatar=DisplayClaim(root,"picture",2048);
            if (root.TryGetProperty("https://api.openai.com/auth", out var authClaims) &&
                authClaims.ValueKind == JsonValueKind.Object)
            {
                EnsureUniqueProperties(authClaims);
                plan ??= OptionalString(authClaims, "chatgpt_plan_type", 64);
            }
            if (root.TryGetProperty("https://api.openai.com/profile", out var profileClaims) &&
                profileClaims.ValueKind == JsonValueKind.Object)
            {
                EnsureUniqueProperties(profileClaims);
                email ??= OptionalString(profileClaims, "email", 254);
                displayName ??= DisplayClaim(profileClaims,"name",160) ?? DisplayClaim(profileClaims,"nickname",160);
                username ??= DisplayClaim(profileClaims,"preferred_username",160) ?? DisplayClaim(profileClaims,"username",160);
                avatar ??= DisplayClaim(profileClaims,"picture",2048);
            }
            return new JwtMetadata { Subject = subject, Email = email, Plan = plan, ExpiresAtUtc = expires,
                DisplayName=displayName,Username=username,AvatarUrl=avatar };
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static string? DisplayClaim(JsonElement root,string name,int limit)
    {
        if(!root.TryGetProperty(name,out var value) || value.ValueKind!=JsonValueKind.String)return null;
        var text=value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length>limit || text.Any(char.IsControl) ? null : text;
    }

    private static string? OptionalString(JsonElement container, string name, int maximumLength)
    {
        if (!container.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new UserFacingException(InvalidMessage);
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result)) return null;
        if (result.Length > maximumLength || result.Any(char.IsControl))
            throw new UserFacingException(InvalidMessage);
        return result;
    }

    private static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "未提供邮箱";
        var at = email.IndexOf('@');
        if (at < 1 || at != email.LastIndexOf('@') || at >= email.Length - 1)
            return "未提供邮箱";
        var local = email[..at];
        var domain = email[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var suffix = dot > 0 && dot < domain.Length - 1 && domain.Length - dot <= 16
            ? domain[dot..] : string.Empty;
        return local[..1] + "***@" + domain[..1] + "***" + suffix;
    }

    private static string DisplayPlan(string? plan) => plan?.ToLowerInvariant() switch
    {
        "free" => "Free",
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        _ => "未知"
    };

    private sealed class JwtMetadata
    {
        public string? Subject { get; init; }
        public string? Email { get; init; }
        public string? Plan { get; init; }
        public DateTimeOffset? ExpiresAtUtc { get; init; }
        public string? DisplayName { get; init; }
        public string? Username { get; init; }
        public string? AvatarUrl { get; init; }
    }
}
