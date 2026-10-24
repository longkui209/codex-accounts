using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.LiveSession;

namespace CodexAccountSwitcher.Validation;

public static class AccountUsageSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync(string outputRoot)
    {
        var reports = new List<string>();
        var root = Path.Combine(Path.GetFullPath(outputRoot), "usage-synthetic-" + Guid.NewGuid().ToString("N"));
        var vault = new VaultStore(root);
        var clock = new Clock();
        const string payload = """
            {"plan_type":"plus","email":"private-synthetic@example.test","account_id":"synthetic-usage-account",
             "rate_limit":{"primary_window":{"used_percent":24.5,"limit_window_seconds":18000,"reset_at":1792623600},
                           "secondary_window":{"used_percent":80,"limit_window_seconds":604800,"reset_after_seconds":3600}},
             "code_review_rate_limit":{"primary_window":{"used_percent":0,"limit_window_seconds":604800}}}
            """;
        var id = new string('a', 64);
        var credential = new LiveCredential("synthetic-usage-account", "synthetic-usage-token", "pro",
            new AccountInfo(id, "s***@e***.test", "Pro", clock.GetUtcNow().AddHours(1)));
        var handler = new Handler(payload);
        using var service = new AccountUsageService(vault, (_, _) => Task.FromResult(credential), handler, clock);
        var first = await service.RefreshAsync(id, false, CancellationToken.None);
        Require(first.Status == "ok" && first.Plan == "Plus" && credential.Info.Plan == "Pro", "官方套餐覆盖旧令牌标签且不改写凭据");
        Require(first.Primary?.UsedPercent == 24.5 && first.Primary.LimitWindowSeconds == 18000 &&
                first.Secondary?.ResetsAtUtc == clock.GetUtcNow().AddHours(1) && first.CodeReview?.UsedPercent == 0,
            "真实字段格式保留已用比例、窗口和绝对或相对重置时间");
        Require(!JsonSerializer.Serialize(first).Contains("private-synthetic") && !JsonSerializer.Serialize(first).Contains("synthetic-usage-token"),
            "用量结果不包含联系人、原始标识或凭据");
        reports.Add("PASS: 官方套餐、额度窗口、重置时间和零用量准确解析，旧 Pro 标签与私密字段不会混入结果。");
        await service.RefreshAsync(id, false, CancellationToken.None);
        Require(handler.Calls == 1, "一分钟内自动刷新复用加密缓存");
        clock.Now = clock.Now.AddSeconds(100);
        Require(service.Cached(id)?.Stale == true, "过期记录有明确标识");
        handler.Status = HttpStatusCode.Unauthorized;
        var failed = await service.RefreshAsync(id, true, CancellationToken.None);
        Require(failed.Status == "reauth-required" && failed.Stale && failed.Plan == "Plus" &&
                failed.UpdatedAtUtc == first.UpdatedAtUtc && failed.AttemptedAtUtc > first.AttemptedAtUtc,
            "401 保留上次成功值与时间，不能显示伪造的新鲜零值");
        var cachedCalls = handler.Calls;
        await service.RefreshAsync(id, false, CancellationToken.None);
        Require(handler.Calls == cachedCalls, "失败后同样有刷新间隔保护");
        reports.Add("PASS: 缓存一分钟限频，过期与认证失败可区分，失败不清零额度或冒充成功更新。");
        handler.Status = HttpStatusCode.OK;
        handler.Payload = "{\"plan_type\":\"free\",\"rate_limit\":{\"primary_window\":{\"used_percent\":null},\"secondary_window\":{\"used_percent\":\"bad\"}}}";
        var free = await service.RefreshAsync(id, true, CancellationToken.None);
        Require(free.Plan == "Free" && free.Status == "ok" && free.Primary?.UsedPercent is null && free.Secondary?.UsedPercent is null,
            "套餐变更实时反映，缺失或异常额度不当作零");
        handler.Payload = "[]";
        var invalid = await service.RefreshAsync(id, true, CancellationToken.None);
        Require(invalid.Status == "unavailable" && invalid.Plan == "Free" && invalid.Stale, "异常响应保留可靠旧值");
        Require(handler.DestinationValid && handler.CredentialsValid, "只向固定官方用量接口使用对应账号认证");
        try { service.Cached("../unsafe"); await service.RefreshAsync("../unsafe", true, CancellationToken.None); throw new InvalidOperationException(); }
        catch (UserFacingException) { }
        foreach (var file in Directory.EnumerateFiles(root, "*.dpapi", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var visible = Encoding.UTF8.GetString(bytes);
            Require(!visible.Contains("private-synthetic") && !visible.Contains("usedPercent") && !visible.Contains("synthetic-usage-token"),
                "用量与身份缓存只以加密形式持久化");
        }
        reports.Add("PASS: 套餐变化、缺失额度、异常 JSON、固定上游、路径校验和 DPAPI 数据保护通过。");
        using var newTier = JsonDocument.Parse("{\"plan_type\":\"synthetic_new_tier\"}");
        using var privateValue = JsonDocument.Parse("{\"plan_type\":\"private@example.test\"}");
        Require(AccountUsageService.Parse(newTier.RootElement, clock.GetUtcNow()).Plan == "synthetic_new_tier" &&
                AccountUsageService.Parse(privateValue.RootElement, clock.GetUtcNow()).Plan is null,
            "新套餐代码可见但不能将任意联系人字符串当作套餐返回");
        reports.Add("PASS: 新官方套餐代码保留，越界或含联系信息的套餐字段不返回。");
        return reports;
    }
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new UserFacingException("隔离用量验证失败：" + name + "。");
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(string payload) : HttpMessageHandler
    {
        public string Payload { get; set; } = payload;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Calls { get; private set; }
        public bool DestinationValid { get; private set; } = true;
        public bool CredentialsValid { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            DestinationValid &= request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://chatgpt.com/backend-api/wham/usage";
            CredentialsValid &= request.Headers.Authorization?.Parameter == "synthetic-usage-token" &&
                request.Headers.TryGetValues("ChatGPT-Account-Id", out var accounts) && accounts.Single() == "synthetic-usage-account";
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Payload, Encoding.UTF8, "application/json") });
        }
    }
}
