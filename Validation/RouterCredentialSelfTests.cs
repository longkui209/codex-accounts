using System.Security.Cryptography;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Only newly created synthetic files, DPAPI vaults and an in-memory provider are used.</summary>
public static class RouterCredentialSelfTests
{
    public static IReadOnlyList<string> Run(string outputRoot) =>
        Task.Run(() => RunAsync(outputRoot)).GetAwaiter().GetResult();

    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var root = Path.Combine(Path.GetFullPath(outputRoot), "router-credential-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FilePrivacy.ApplyDirectory(root);
        var reports = new List<string>();

        using (var data = Fixture.Create(root, "latest-desktop"))
        {
            var provider = new FakeProvider((id, _, _) => Task.FromResult(ReadCredential(data.Vault, id)));
            var routing = new RouterCredentials(data.Vault, data.Home, provider, data.AProfile, data.Clock);
            var first = await routing.GetAsync(data.AProfile, CancellationToken.None);
            Require(routing.Authorize(first.AccessToken, first.AccountId), "最新桌面 token 授权");
            Require(!routing.Authorize(first.AccessToken, "synthetic-b"), "错误账号 ID 拒绝");
            Require(!routing.Authorize("Bearer " + first.AccessToken, first.AccountId), "接口仅接收纯 token");
            Require(!routing.Authorize(null, first.AccountId), "无 token 拒绝");
            var b = ReadCredential(data.Vault, data.BProfile);
            Require(!routing.Authorize(b.AccessToken, b.AccountId), "不能使用选中 B 授权本地入口");

            var secondBytes = Auth(data.Clock.Now, "a", "synthetic-native-rotation");
            try
            {
                File.WriteAllBytes(data.AuthPath, secondBytes);
                var second = await routing.GetAsync(data.AProfile, CancellationToken.None);
                Require(second.AccessToken != first.AccessToken &&
                    !routing.Authorize(first.AccessToken, first.AccountId) &&
                    routing.Authorize(second.AccessToken, second.AccountId), "不继续信任启动 token");
                Require(provider.Calls == 0, "桌面 A 获取从不访问 saved provider");
                using var unchangedVaultA = data.Vault.Read(data.AProfile);
                var originalVault = unchangedVaultA.CopyBytes();
                try { Require(CryptographicOperations.FixedTimeEquals(originalVault, data.OriginalA), "原生轮换不独立写 Vault"); }
                finally { CryptographicOperations.ZeroMemory(originalVault); }
                await Rejected(() => routing.RefreshAsync(data.AProfile, CancellationToken.None));
                EqualFile(data.AuthPath, secondBytes);
                Require(provider.Calls == 0, "桌面 A 自刷新固定拒绝");
            }
            finally { CryptographicOperations.ZeroMemory(secondBytes); }
            reports.Add("PASS: 最新桌面授权、拒绝 B/旧 token/错误 Aid、A 只读及桌面自刷新拒绝。");
        }

        using (var data = Fixture.Create(root, "saved-profile"))
        {
            var provider = new FakeProvider((id, force, _) =>
            {
                if (force)
                {
                    var rotated = Auth(data.Clock.Now, "b", "synthetic-vault-rotation");
                    try { using var snapshot = AuthSnapshot.FromBytes(rotated); data.Vault.Save(snapshot); }
                    finally { CryptographicOperations.ZeroMemory(rotated); }
                }
                return Task.FromResult(ReadCredential(data.Vault, id));
            });
            var routing = new RouterCredentials(data.Vault, data.Home, provider, data.AProfile, data.Clock);
            var first = await routing.GetAsync(data.BProfile, CancellationToken.None);
            Require(provider.Calls == 1 && !provider.LastForce && first.Info.IdentityKey == data.BProfile, "B 的普通凭据调用");
            var next = await routing.RefreshAsync(data.BProfile, CancellationToken.None);
            Require(provider.Calls == 2 && provider.LastForce && first.AccessToken != next.AccessToken, "B 的刷新一次 force");
            EqualFile(data.AuthPath, data.OriginalA);
            Require(!routing.Authorize(next.AccessToken, next.AccountId), "轮换后的 B 仍不能充当本地认证");
            reports.Add("PASS: B 普通/强制调用及仅 Vault 轮换，原生桌面文件保持逐字节一致。");
        }

        foreach (var test in new[] { "expired", "issuer", "audience", "owner", "keyring" })
        {
            using var data = Fixture.Create(root, test);
            var provider = new FakeProvider((id, _, _) => Task.FromResult(ReadCredential(data.Vault, id)));
            var routing = new RouterCredentials(data.Vault, data.Home, provider, data.AProfile, data.Clock);
            var bytes = Auth(data.Clock.Now, test == "owner" ? "b" : "a", "synthetic-unsupported",
                minutes: test == "expired" ? -1 : 60,
                issuer: test == "issuer" ? "https://example.invalid" : "https://auth.openai.com",
                audience: test == "audience" ? "https://example.invalid" : "app_synthetic_client");
            try
            {
                File.WriteAllBytes(data.AuthPath, bytes);
                if (test == "keyring") File.WriteAllText(Path.Combine(data.Home, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
                var credential = ReadCredential(data.Vault, data.AProfile);
                Require(!routing.Authorize(credential.AccessToken, credential.AccountId), "不安全桌面来源拒绝");
                await Rejected(() => routing.GetAsync(data.AProfile, CancellationToken.None));
                EqualFile(data.AuthPath, bytes);
                Require(provider.Calls == 0, "失败不转向其他账号或网络");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        reports.Add("PASS: 过期、非官方 issuer、无效 audience、不同 owner、keyring 来源全部拒绝。");

        using (var data = Fixture.Create(root, "stale-saved"))
        {
            var provider = new FakeProvider((id, _, _) =>
            {
                var stale = ReadCredential(data.Vault, id);
                var updated = Auth(data.Clock.Now, "b", "synthetic-concurrent-relogin");
                try { using var snapshot = AuthSnapshot.FromBytes(updated); data.Vault.Save(snapshot); }
                finally { CryptographicOperations.ZeroMemory(updated); }
                return Task.FromResult(stale);
            });
            var routing = new RouterCredentials(data.Vault, data.Home, provider, data.AProfile, data.Clock);
            await Rejected(() => routing.GetAsync(data.BProfile, CancellationToken.None));
            EqualFile(data.AuthPath, data.OriginalA);
            reports.Add("PASS: saved provider 返回过时凭据时拒绝，不覆盖并发重新登录或桌面文件。");
        }
        return reports;
    }

    private static LiveCredential ReadCredential(VaultStore vault, string id)
    {
        using var snapshot = vault.Read(id);
        var bytes = snapshot.CopyBytes();
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var tokens = document.RootElement.GetProperty("tokens");
            return new LiveCredential(tokens.GetProperty("account_id").GetString()!,
                tokens.GetProperty("access_token").GetString()!, "plus", snapshot.Info);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Auth(DateTimeOffset now, string id, string nonce, int minutes = 60,
        string issuer = "https://auth.openai.com", string audience = "app_synthetic_client")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = issuer, ["aud"] = audience, ["sub"] = "synthetic-subject-" + id,
            ["iat"] = now.ToUnixTimeSeconds(), ["exp"] = now.AddMinutes(minutes).ToUnixTimeSeconds(),
            ["email"] = "synthetic-" + id + "@example.test", ["syntheticNonce"] = nonce,
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = "synthetic-" + id, ["chatgpt_plan_type"] = "plus"
            }
        });
        try
        {
            var jwt = "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(payload).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_') + ".syntheticSignature";
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_mode = "chatgpt",
                tokens = new { access_token = jwt, id_token = jwt, refresh_token = "synthetic-refresh-" + nonce, account_id = "synthetic-" + id }
            });
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static void EqualFile(string path, byte[] expected)
    {
        var bytes = File.ReadAllBytes(path);
        try { Require(CryptographicOperations.FixedTimeEquals(bytes, expected), "合成桌面认证文件保持一致"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task Rejected(Func<Task<LiveCredential>> action)
    {
        try { _ = await action(); } catch (UserFacingException) { return; }
        throw new UserFacingException("合成模型路由验证失败：未拒绝不安全凭据。");
    }
    private static void Require(bool ok, string check)
    {
        if (!ok) throw new UserFacingException("合成模型路由验证失败：" + check + "。");
    }
    private sealed class FakeProvider(Func<string, bool, CancellationToken, Task<LiveCredential>> operation) : ILiveCredentials
    {
        public int Calls { get; private set; }
        public bool LastForce { get; private set; }
        public Task<LiveCredential> GetAsync(string profileId, bool forceRefresh, CancellationToken cancellation)
        {
            Calls++;
            LastForce = forceRefresh;
            return operation(profileId, forceRefresh, cancellation);
        }
    }
    private sealed class FrozenClock : TimeProvider
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture : IDisposable
    {
        public string Home { get; private init; } = "";
        public string AuthPath => Path.Combine(Home, "auth.json");
        public VaultStore Vault { get; private init; } = null!;
        public FrozenClock Clock { get; private init; } = null!;
        public byte[] OriginalA { get; private init; } = [];
        public string AProfile { get; private init; } = "";
        public string BProfile { get; private init; } = "";
        public static Fixture Create(string root, string name)
        {
            var directory = Path.Combine(root, name);
            var home = Path.Combine(directory, "home");
            Directory.CreateDirectory(home);
            FilePrivacy.ApplyDirectory(home);
            var vault = new VaultStore(Path.Combine(directory, "vault"));
            var clock = new FrozenClock();
            var a = Auth(clock.Now, "a", "synthetic-initial");
            var b = Auth(clock.Now, "b", "synthetic-initial");
            try
            {
                using var first = AuthSnapshot.FromBytes(a);
                using var second = AuthSnapshot.FromBytes(b);
                var firstId = vault.Save(first, "Synthetic desktop A").Id;
                var secondId = vault.Save(second, "Synthetic model B").Id;
                File.WriteAllBytes(Path.Combine(home, "auth.json"), a);
                FilePrivacy.ApplyFile(Path.Combine(home, "auth.json"));
                return new Fixture { Home = home, Vault = vault, Clock = clock, OriginalA = a,
                    AProfile = firstId, BProfile = secondId };
            }
            finally { CryptographicOperations.ZeroMemory(b); }
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(OriginalA);
    }
}
