using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.Validation;

/// <summary>Offline only: every credential is synthetic and every HTTP call uses an in-memory handler.</summary>
public static class LiveCredentialSelfTests
{
    // The application's self-test entry can run on WPF's STA thread. Start the
    // asynchronous suite outside its dispatcher before synchronously joining it.
    public static IReadOnlyList<string> Run(string outputRoot) =>
        Task.Run(() => RunAsync(outputRoot)).GetAwaiter().GetResult();

    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var root = Path.Combine(Path.GetFullPath(outputRoot), "live-credential-synthetic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FilePrivacy.ApplyDirectory(root);
        var reports = new List<string>();

        using (var data = Fixture.Create(root, "fresh", 60))
        using (var handler = new FakeHandler((_, _) => throw new InvalidOperationException("SYNTHETIC-NO-NETWORK")))
        using (var http = new HttpClient(handler))
        using (var provider = new LiveCredentials(data.Vault, http, data.Clock))
        {
            var result = await provider.GetAsync(data.ProfileId, false, CancellationToken.None);
            Require(result.AccountId == Fixture.AccountId && result.Info.IdentityKey == data.ProfileId && handler.Calls == 0,
                "有效期大于五分钟时不发送 HTTP");
            RequireUnchanged(data);
            reports.Add("PASS: 可用缓存直接返回，不联网且不修改凭据。");
        }

        using (var data = Fixture.Create(root, "rotation", 2, "app_synthetic_actual_client"))
        using (var handler = new FakeHandler(async (request, cancellation) =>
        {
            await ValidateRequest(request, "app_synthetic_actual_client", cancellation);
            return Reply(data.Clock.Now, refresh: "synthetic-refresh-rotated", plan: "pro",
                clientId: "app_synthetic_actual_client");
        }))
        using (var http = new HttpClient(handler))
        using (var provider = new LiveCredentials(data.Vault, http, data.Clock))
        {
            var result = await provider.GetAsync(data.ProfileId, false, CancellationToken.None);
            Require(result.Plan == "pro" && result.Info.Plan == "Pro" && handler.Calls == 1, "刷新计划及响应");
            Require(StoredRefresh(data) == "synthetic-refresh-rotated", "轮换 refresh token 持久化");
            Require(data.Vault.List().Single().Label == "Synthetic saved label", "刷新保留名称");
            Require(data.Vault.LatestBackupFileName is null &&
                Directory.EnumerateFiles(data.Vault.DirectoryPath, "refresh-vault-backup-*.dpapi").Any(),
                "加密 checkpoint 不覆盖桌面恢复备份索引");
            reports.Add("PASS: 五分钟前刷新、实际 audience 客户端 ID、轮换凭据和名称持久化。");
        }

        using(var data=Fixture.Create(root,"official-metadata",2))
        {
            var node=System.Text.Json.Nodes.JsonNode.Parse(data.Original)!;
            node["tokens"]!["oauth_provenance"]=new System.Text.Json.Nodes.JsonObject{["issuer"]="https://auth.openai.com",["client_id"]=LiveCredentials.DefaultClientId};
            node["tokens"]!["login_owner_id"]="synthetic-owner-preserved";
            var bytes=JsonSerializer.SerializeToUtf8Bytes(node);
            try{using var snapshot=AuthSnapshot.FromBytes(bytes);data.Vault.Save(snapshot);}finally{CryptographicOperations.ZeroMemory(bytes);}
            using var handler=new FakeHandler((_,_)=>Task.FromResult(Reply(data.Clock.Now)));
            using var http=new HttpClient(handler);using var provider=new LiveCredentials(data.Vault,http,data.Clock);
            _=await provider.GetAsync(data.ProfileId,false,CancellationToken.None);
            using var updated=data.Vault.Read(data.ProfileId);var encoded=updated.CopyBytes();
            try
            {
                using var document=JsonDocument.Parse(encoded);var tokens=document.RootElement.GetProperty("tokens");
                Require(tokens.GetProperty("login_owner_id").GetString()=="synthetic-owner-preserved"&&tokens.GetProperty("oauth_provenance").GetProperty("client_id").GetString()==LiveCredentials.DefaultClientId,"刷新保留官方 OAuth 元数据");
            }
            finally{CryptographicOperations.ZeroMemory(encoded);}
            using(var current=data.Vault.Read(data.ProfileId))
            {
                var original=current.CopyBytes();
                try
                {
                    var altered=System.Text.Json.Nodes.JsonNode.Parse(original)!;
                    altered["tokens"]!["oauth_provenance"]!["client_id"]="app_synthetic_wrong_client";
                    var invalid=JsonSerializer.SerializeToUtf8Bytes(altered);
                    try{using var snapshot=AuthSnapshot.FromBytes(invalid);data.Vault.Save(snapshot);}finally{CryptographicOperations.ZeroMemory(invalid);}
                }
                finally{CryptographicOperations.ZeroMemory(original);}
            }
            await Rejected(()=>provider.GetAsync(data.ProfileId,true,CancellationToken.None));
            Require(handler.Calls==1,"OAuth 客户端与 audience 不匹配时不联网");
            reports.Add("PASS: 官方 OAuth 元数据与登录归属标记在合成刷新后保留，客户端身份核对仍生效。");
        }

        using (var data = Fixture.Create(root, "forced", 60))
        using (var handler = new FakeHandler(async (request, cancellation) =>
        {
            await ValidateRequest(request, LiveCredentials.DefaultClientId, cancellation);
            return Reply(data.Clock.Now, omitId: true, omitRefresh: true);
        }))
        using (var http = new HttpClient(handler))
        using (var provider = new LiveCredentials(data.Vault, http, data.Clock))
        {
            _ = await provider.GetAsync(data.ProfileId, true, CancellationToken.None);
            Require(handler.Calls == 1 && StoredRefresh(data) == "synthetic-refresh-original",
                "强制刷新和 optional id/refresh 保留");
            reports.Add("PASS: 强制刷新及官方 optional token 字段保留。");
        }

        using (var data = Fixture.Create(root, "concurrent", 2))
        using (var handler = new FakeHandler(async (request, cancellation) =>
        {
            await ValidateRequest(request, LiveCredentials.DefaultClientId, cancellation);
            await Task.Delay(80, cancellation);
            return Reply(data.Clock.Now);
        }))
        using (var http = new HttpClient(handler))
        using (var first = new LiveCredentials(data.Vault, http, data.Clock))
        using (var second = new LiveCredentials(data.Vault, http, data.Clock))
        {
            var tasks = new[]
            {
                first.GetAsync(data.ProfileId, false, CancellationToken.None),
                second.GetAsync(data.ProfileId, false, CancellationToken.None)
            };
            _ = await Task.WhenAll(tasks);
            Require(handler.Calls == 1 && StoredRefresh(data) == "synthetic-refresh-rotated",
                "跨实例刷新序列化及第二次重读新缓存");
            reports.Add("PASS: 并发调用串行，后续请求复用最新轮换缓存。");
        }

        using (var data = Fixture.Create(root, "relogin-race", 2))
        using (var handler = new FakeHandler((_, _) =>
        {
            var bytes = Auth(data.Clock.Now, 90, refresh: "synthetic-official-relogin");
            try { using var relogin = AuthSnapshot.FromBytes(bytes); data.Vault.Save(relogin); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return Task.FromResult(Reply(data.Clock.Now));
        }))
        using (var http = new HttpClient(handler))
        using (var provider = new LiveCredentials(data.Vault, http, data.Clock))
        {
            await Rejected(() => provider.GetAsync(data.ProfileId, true, CancellationToken.None));
            Require(StoredRefresh(data) == "synthetic-official-relogin", "不能覆盖并发重新登录");
            reports.Add("PASS: 完整快照 compare-and-save 保留并发官方重新登录。");
        }

        foreach (var failure in new[] { "issuer", "subject", "account", "expired", "redirect", "http-error" })
        {
            using var data = Fixture.Create(root, failure, 2, issuer: failure == "issuer" ? "https://example.invalid" : "https://auth.openai.com");
            using var handler = new FakeHandler((request, _) => Task.FromResult(failure switch
            {
                "subject" => Reply(data.Clock.Now, subject: "synthetic-other-subject"),
                "account" => Reply(data.Clock.Now, account: "synthetic-other-account"),
                "expired" => Reply(data.Clock.Now, minutes: -1),
                "redirect" => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    RequestMessage = request, Headers = { Location = new Uri("https://example.invalid/") },
                    Content = new StringContent("SYNTHETIC-RAW-ERROR")
                },
                _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request, Content = new StringContent("SYNTHETIC-RAW-ERROR")
                }
            }));
            using var http = new HttpClient(handler);
            using var provider = new LiveCredentials(data.Vault, http, data.Clock);
            await Rejected(() => provider.GetAsync(data.ProfileId, true, CancellationToken.None));
            RequireUnchanged(data);
            if (failure == "issuer") Require(handler.Calls == 0, "非法 issuer 在 HTTP 前拒绝");
        }
        reports.Add("PASS: 非官方 issuer、变化身份/账号、过期响应、重定向及 API 错误均拒绝保存。");

        using (var data = Fixture.Create(root, "cancel", 2))
        using (var handler = new FakeHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("SYNTHETIC-RAW-ERROR");
        }))
        using (var http = new HttpClient(handler))
        using (var provider = new LiveCredentials(data.Vault, http, data.Clock))
        using (var cancellation = new CancellationTokenSource(80))
        {
            await Rejected(() => provider.GetAsync(data.ProfileId, true, cancellation.Token));
            RequireUnchanged(data);
            reports.Add("PASS: 取消 HTTP 刷新时不重试、不保存，并使用固定重新登录文案。");
        }
        return reports;
    }

    private static async Task ValidateRequest(HttpRequestMessage request, string clientId, CancellationToken cancellation)
    {
        Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://auth.openai.com/oauth/token" &&
            request.Content?.Headers.ContentType?.MediaType == "application/json" && request.Headers.Authorization is null,
            "固定官方 JSON grant endpoint");
        var bytes = await request.Content!.ReadAsByteArrayAsync(cancellation);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            Require(root.EnumerateObject().Count() == 3 && root.GetProperty("client_id").GetString() == clientId &&
                root.GetProperty("grant_type").GetString() == "refresh_token" &&
                root.GetProperty("refresh_token").GetString() == "synthetic-refresh-original", "官方 refresh grant 参数");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static HttpResponseMessage Reply(DateTimeOffset now, string refresh = "synthetic-refresh-rotated",
        string plan = "plus", string subject = Fixture.Subject, string account = Fixture.AccountId,
        int minutes = 60, bool omitId = false, bool omitRefresh = false, string clientId = LiveCredentials.DefaultClientId)
    {
        var result = new Dictionary<string, object>
        {
            ["access_token"] = Jwt(now.AddMinutes(minutes), subject, account, plan, clientId)
        };
        if (!omitId) result["id_token"] = Jwt(now.AddMinutes(minutes), subject, account, plan, clientId);
        if (!omitRefresh) result["refresh_token"] = refresh;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ClearingContent(JsonSerializer.SerializeToUtf8Bytes(result)) };
    }

    private static byte[] Auth(DateTimeOffset now, int minutes, string clientId = LiveCredentials.DefaultClientId,
        string refresh = "synthetic-refresh-original", string issuer = "https://auth.openai.com")
    {
        var jwt = Jwt(now.AddMinutes(minutes), Fixture.Subject, Fixture.AccountId, "plus", clientId, issuer);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { access_token = jwt, id_token = jwt, refresh_token = refresh, account_id = Fixture.AccountId }
        });
    }

    private static string Jwt(DateTimeOffset expires, string subject, string account, string plan, string clientId,
        string issuer = "https://auth.openai.com")
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = issuer, ["aud"] = clientId, ["sub"] = subject, ["email"] = "synthetic@example.test",
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_account_id"] = account, ["chatgpt_plan_type"] = plan
            }
        });
        try { return "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".syntheticSignature"; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string StoredRefresh(Fixture data)
    {
        using var snapshot = data.Vault.Read(data.ProfileId);
        var bytes = snapshot.CopyBytes();
        try { using var doc = JsonDocument.Parse(bytes); return doc.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString()!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireUnchanged(Fixture data)
    {
        using var snapshot = data.Vault.Read(data.ProfileId);
        var bytes = snapshot.CopyBytes();
        try { Require(CryptographicOperations.FixedTimeEquals(bytes, data.Original), "失败不覆盖账号库凭据"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task Rejected(Func<Task<LiveCredential>> action)
    {
        try { _ = await action(); }
        catch (OperationCanceledException) { return; }
        catch (UserFacingException error)
        {
            Require(!error.Message.Contains("SYNTHETIC-RAW", StringComparison.Ordinal), "原始错误不会泄露");
            return;
        }
        throw new UserFacingException("合成凭据生命周期验证失败：未拒绝不安全刷新。");
    }

    private static void Require(bool valid, string check)
    {
        if (!valid) throw new UserFacingException("合成凭据生命周期验证失败：" + check + "。");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return callback(request, cancellationToken);
        }
    }

    private sealed class ClearingContent : ByteArrayContent
    {
        private readonly byte[] ownedBytes;
        public ClearingContent(byte[] bytes) : base(bytes) => ownedBytes = bytes;
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { CryptographicOperations.ZeroMemory(ownedBytes); }
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        public DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public const string AccountId = "synthetic-live-account", Subject = "synthetic-live-subject";
        public VaultStore Vault { get; private init; } = null!;
        public FrozenClock Clock { get; private init; } = null!;
        public byte[] Original { get; private init; } = [];
        public string ProfileId { get; private init; } = "";
        public static Fixture Create(string root, string name, int minutes,
            string clientId = LiveCredentials.DefaultClientId, string issuer = "https://auth.openai.com")
        {
            var vault = new VaultStore(Path.Combine(root, name));
            var clock = new FrozenClock();
            var bytes = Auth(clock.Now, minutes, clientId, issuer: issuer);
            using var snapshot = AuthSnapshot.FromBytes(bytes);
            var summary = vault.Save(snapshot, "Synthetic saved label");
            return new Fixture { Vault = vault, Clock = clock, Original = bytes, ProfileId = summary.Id };
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(Original);
    }
}
